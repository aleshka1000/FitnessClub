using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WebApi.RateLimiting;

namespace WebApi.Tests;

[TestClass]
public class ResilientRateLimiterTests
{
    [TestMethod]
    public async Task FallbackLimit_IsSharedByUsers_AndExpiresAtOneSecond()
    {
        var clock = new MonotonicTestTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);
        for (var i = 0; i < 10; i++) Assert.IsTrue(await limiter.TryAcquireAsync(i));
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        Assert.IsFalse(await limiter.TryAcquireAsync(2));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task ConcurrentRequestsForDifferentUsers_OnlyTenAreAllowed()
    {
        var limiter = new InMemoryFallbackRateLimiter(new MonotonicTestTimeProvider());
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(userId => Task.Run(() => limiter.TryAcquireAsync(userId))));
        Assert.AreEqual(10, results.Count(x => x));
    }

    [TestMethod]
    public async Task SlidingWindow_ExpiresRequestsIndividually()
    {
        var clock = new MonotonicTestTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);
        for (var i = 0; i < 6; i++) Assert.IsTrue(await limiter.TryAcquireAsync(1));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (var i = 0; i < 4; i++) Assert.IsTrue(await limiter.TryAcquireAsync(2));
        Assert.IsFalse(await limiter.TryAcquireAsync(3));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (var i = 0; i < 6; i++) Assert.IsTrue(await limiter.TryAcquireAsync(3));
        Assert.IsFalse(await limiter.TryAcquireAsync(2));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        for (var i = 0; i < 4; i++) Assert.IsTrue(await limiter.TryAcquireAsync(2));
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task UtcClockChange_DoesNotResetTheLocalWindow()
    {
        var clock = new MonotonicTestTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);
        for (var i = 0; i < 10; i++) Assert.IsTrue(await limiter.TryAcquireAsync(1));
        clock.UtcOffset = TimeSpan.FromDays(1);
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HealthyRedisRequests_DoNotConsumeFallbackQuota(bool timeout)
    {
        var primary = new Mock<IUserRateLimiter>();
        var available = true;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(true) : Task.FromException<bool>(Unavailable(timeout)));
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(new MonotonicTestTimeProvider()), NullLogger<ResilientUserRateLimiter>.Instance);
        for (var i = 0; i < 50; i++) Assert.IsTrue(await limiter.TryAcquireAsync(1));
        available = false;
        for (var i = 0; i < 10; i++) Assert.IsTrue(await limiter.TryAcquireAsync(1));
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task RedisDenial_IsNotBypassed_AndDoesNotConsumeLocalQuota()
    {
        var primary = new Mock<IUserRateLimiter>();
        var available = true;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(false) : Task.FromException<bool>(Unavailable()));
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(new MonotonicTestTimeProvider()), NullLogger<ResilientUserRateLimiter>.Instance);
        for (var i = 0; i < 60; i++) Assert.IsFalse(await limiter.TryAcquireAsync(1));
        available = false;
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task Recovery_UsesRedisAgain_AndLogsOnlyTransitions()
    {
        var primary = new Mock<IUserRateLimiter>();
        var available = false;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(true) : Task.FromException<bool>(Unavailable()));
        var logger = new Mock<ILogger<ResilientUserRateLimiter>>();
        var clock = new MonotonicTestTimeProvider();
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(clock), logger.Object);
        for (var i = 0; i < 10; i++) Assert.IsTrue(await limiter.TryAcquireAsync(1));
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        available = true;
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
        available = false;
        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
        Assert.AreEqual(2, logger.Invocations.Count(x => x.Method.Name == "Log" && (LogLevel)x.Arguments[0] == LogLevel.Warning));
        Assert.AreEqual(1, logger.Invocations.Count(x => x.Method.Name == "Log" && (LogLevel)x.Arguments[0] == LogLevel.Information));
    }

    [TestMethod]
    public async Task CancellationAndUnexpectedRedisErrors_AreNotMasked()
    {
        var primary = new Mock<IUserRateLimiter>(MockBehavior.Strict);
        var fallback = new InMemoryFallbackRateLimiter(new MonotonicTestTimeProvider());
        var limiter = new ResilientUserRateLimiter(primary.Object, fallback, NullLogger<ResilientUserRateLimiter>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => limiter.TryAcquireAsync(1, cancellation.Token));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fallback.TryAcquireAsync(1, cancellation.Token));
        primary.VerifyNoOtherCalls();
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => limiter.TryAcquireAsync(1));
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new RedisServerException("Invalid script."));
        await Assert.ThrowsExceptionAsync<RedisServerException>(() => limiter.TryAcquireAsync(1));
    }

    private static Exception Unavailable(bool timeout = false) => timeout
        ? new RedisTimeoutException("Redis timeout.", CommandStatus.Unknown)
        : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable.");

    private sealed class MonotonicTestTimeProvider : TimeProvider
    {
        private long _ticks;
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp()).Add(UtcOffset);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
