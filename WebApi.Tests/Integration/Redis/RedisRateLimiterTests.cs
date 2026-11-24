using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using WebApi.RateLimiting;

namespace WebApi.Tests;

[TestClass]
public class RedisRateLimiterTests
{
    [TestMethod]
    public async Task ConcurrentInstances_ShareOneAtomicLimit()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var connection = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var prefix = $"test:{Guid.NewGuid():N}";
        var first = new RedisUserRateLimiter(connection, prefix);
        var second = new RedisUserRateLimiter(connection, prefix);

        // Act
        var allowed = await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(i => (i % 2 == 0 ? first : second).TryAcquireAsync(1)));

        // Assert
        Assert.AreEqual(60, allowed.Count(x => x));
        Assert.IsTrue(await first.TryAcquireAsync(2));
        Assert.IsTrue(await connection.GetDatabase().KeyTimeToLiveAsync($"{prefix}:1") > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task ExpiredRequests_AreRemovedFromSlidingWindow()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var connection = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var prefix = $"test:{Guid.NewGuid():N}";
        var key = $"{prefix}:1";
        var db = connection.GetDatabase();
        await db.SortedSetAddAsync(key, Enumerable.Range(0, 60)
                .Select(i => new SortedSetEntry($"old-{i}", 0)).ToArray());
        var limiter = new RedisUserRateLimiter(connection, prefix);

        // Act & Assert
        Assert.IsTrue(await limiter.TryAcquireAsync(1));

        // Assert
        Assert.AreEqual(1L, await db.SortedSetLengthAsync(key));
    }
}
