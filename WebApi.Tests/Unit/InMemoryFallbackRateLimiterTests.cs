using WebApi.RateLimiting;

namespace WebApi.Tests;

[TestClass]
public class InMemoryFallbackRateLimiterTests
{
    [TestMethod]
    public async Task FallbackLimit_IsSharedByUsers_AndExpiresAtOneSecond()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);

        // Act & Assert
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(i));

        Assert.IsFalse(await limiter.TryAcquireAsync(1));
        Assert.IsFalse(await limiter.TryAcquireAsync(2));

        // Arrange
        clock.Advance(TimeSpan.FromMilliseconds(999));

        // Act & Assert
        Assert.IsFalse(await limiter.TryAcquireAsync(1));

        // Arrange
        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task ConcurrentRequestsForDifferentUsers_OnlyTenAreAllowed()
    {
        // Arrange
        var limiter = new InMemoryFallbackRateLimiter(new ManualTimeProvider());

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(userId => Task.Run(() => limiter.TryAcquireAsync(userId))));

        // Assert
        Assert.AreEqual(10, results.Count(x => x));
    }

    [TestMethod]
    public async Task SlidingWindow_ExpiresRequestsIndividually()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);

        // Act & Assert
        for (var i = 0; i < 6; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Arrange
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // Act & Assert
        for (var i = 0; i < 4; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(2));

        Assert.IsFalse(await limiter.TryAcquireAsync(3));

        // Arrange
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // Act & Assert
        for (var i = 0; i < 6; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(3));

        Assert.IsFalse(await limiter.TryAcquireAsync(2));

        // Arrange
        clock.Advance(TimeSpan.FromMilliseconds(500));

        // Act & Assert
        for (var i = 0; i < 4; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(2));

        Assert.IsFalse(await limiter.TryAcquireAsync(1));
    }

    [TestMethod]
    public async Task UtcClockChange_DoesNotResetTheLocalWindow()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var limiter = new InMemoryFallbackRateLimiter(clock);

        // Act & Assert
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Arrange
        clock.UtcOffset = TimeSpan.FromDays(1);

        // Act & Assert
        Assert.IsFalse(await limiter.TryAcquireAsync(1));

        // Arrange
        clock.Advance(TimeSpan.FromSeconds(1));

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));
    }
}
