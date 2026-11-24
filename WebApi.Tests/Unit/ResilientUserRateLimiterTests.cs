using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using WebApi.RateLimiting;

namespace WebApi.Tests;

[TestClass]
public class ResilientUserRateLimiterTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HealthyRedisRequests_DoNotConsumeFallbackQuota(bool timeout)
    {
        // Arrange
        var primary = new Mock<IUserRateLimiter>();
        var available = true;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(true) : Task.FromException<bool>(Unavailable(timeout)));
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(new ManualTimeProvider()), NullLogger<ResilientUserRateLimiter>.Instance);

        // Act & Assert
        for (var i = 0; i < 50; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Arrange
        available = false;

        // Act & Assert
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(1));

        Assert.IsFalse(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task RedisDenial_IsNotBypassed_AndDoesNotConsumeLocalQuota()
    {
        // Arrange
        var primary = new Mock<IUserRateLimiter>();
        var available = true;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(false) : Task.FromException<bool>(Unavailable()));
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(new ManualTimeProvider()), NullLogger<ResilientUserRateLimiter>.Instance);

        // Act & Assert
        Assert.IsFalse(await limiter.TryAcquireAsync(1));

        // Arrange
        available = false;

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task Recovery_UsesRedisAgain_AndLogsOnlyTransitions()
    {
        // Arrange
        var primary = new Mock<IUserRateLimiter>();
        var available = false;
        primary.Setup(x => x.TryAcquireAsync(1, It.IsAny<CancellationToken>()))
            .Returns(() => available ? Task.FromResult(true) : Task.FromException<bool>(Unavailable()));
        var logger = new Mock<ILogger<ResilientUserRateLimiter>>();
        var clock = new ManualTimeProvider();
        var limiter = new ResilientUserRateLimiter(primary.Object,
            new InMemoryFallbackRateLimiter(clock), logger.Object);

        // Act & Assert
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(1));

        Assert.IsFalse(await limiter.TryAcquireAsync(1));

        // Arrange
        available = true;

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
        Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Arrange
        available = false;

        // Act & Assert
        Assert.IsFalse(await limiter.TryAcquireAsync(1));

        // Arrange
        clock.Advance(TimeSpan.FromSeconds(1));

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Assert
        Assert.AreEqual(
            2,
            logger.Invocations.Count(
                x => x.Method.Name == "Log" && (LogLevel)x.Arguments[0] == LogLevel.Warning));
        Assert.AreEqual(
            1,
            logger.Invocations.Count(
                x => x.Method.Name == "Log" && (LogLevel)x.Arguments[0] == LogLevel.Information));
    }

    [TestMethod]
    public async Task CancellationAndUnexpectedRedisErrors_AreNotMasked()
    {
        // Arrange
        var primary = new Mock<IUserRateLimiter>(MockBehavior.Strict);
        var fallback = new InMemoryFallbackRateLimiter(new ManualTimeProvider());
        var limiter = new ResilientUserRateLimiter(
            primary.Object,
            fallback,
            NullLogger<ResilientUserRateLimiter>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act & Assert
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => limiter.TryAcquireAsync(
                1,
                cancellation.Token));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => fallback.TryAcquireAsync(
                1,
                cancellation.Token));

        // Assert
        primary.VerifyNoOtherCalls();

        // Arrange
        primary.Setup(x => x.TryAcquireAsync(
                1,
                It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => limiter.TryAcquireAsync(1));

        // Arrange
        primary.Setup(x => x.TryAcquireAsync(
                1,
                It.IsAny<CancellationToken>())).ThrowsAsync(new RedisServerException("Invalid script."));

        // Act & Assert
        await Assert.ThrowsExceptionAsync<RedisServerException>(() => limiter.TryAcquireAsync(1));
    }

    private static Exception Unavailable(bool timeout = false) => timeout
        ? new RedisTimeoutException("Redis timeout.", CommandStatus.Unknown)
        : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unavailable.");
}
