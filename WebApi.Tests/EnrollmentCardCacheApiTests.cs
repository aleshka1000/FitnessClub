using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using BusinessLogicLayer.Models;
using Common.Caching;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentCardCacheApiTests
{
    [TestMethod]
    public async Task RepeatedGet_UsesOnlyVersionsQuery_AndKeepsExpiry()
    {
        var queries = new CardQueries();
        await using var host = new EnrollmentTestHost(queries);
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await WaitlistTestData.IdAsync(created);
        var key = await KeyAsync(host, scenario.UserId, id);
        queries.Commands.Clear();

        var first = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(2, queries.Commands.Count);
        Assert.AreEqual(1, queries.Commands.Count(sql => sql.Contains("JOIN classes", StringComparison.Ordinal)));
        var redis = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var expiry = await redis.KeyExpireTimeAsync(key);
        var ttl = await redis.KeyTimeToLiveAsync(key);
        Assert.IsNotNull(expiry);
        Assert.IsTrue(ttl > TimeSpan.Zero && ttl <= TimeSpan.FromMinutes(5));
        var cached = await host.Factory.Services.GetRequiredService<ICacheService>().GetAsync<EnrollmentDetailsResult>(key);
        Assert.IsNotNull(cached);
        Assert.AreEqual(id, cached.Id);

        queries.Commands.Clear();
        var second = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(first.GetRawText(), second.GetRawText());
        Assert.AreEqual(1, queries.Commands.Count);
        Assert.IsFalse(queries.Commands.Single().Contains("JOIN classes", StringComparison.Ordinal));
        Assert.AreEqual(expiry, await redis.KeyExpireTimeAsync(key));

        var other = await host.AddUserAsync();
        using var client = host.Client(other);
        using var foreign = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [TestMethod]
    public async Task MalformedCache_IsReplacedWithFreshDatabaseCard()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await WaitlistTestData.IdAsync(created);
        var key = await KeyAsync(host, scenario.UserId, id);
        var redis = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.StringSetAsync(key, "invalid-json", TimeSpan.FromMinutes(5));
        var card = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(8, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        var restored = await host.Factory.Services.GetRequiredService<ICacheService>().GetAsync<EnrollmentDetailsResult>(key);
        Assert.IsNotNull(restored);
        Assert.AreEqual(8, restored.Pass.RemainingVisits);
    }

    [TestMethod]
    public async Task ClassChanges_KeepCachedCardUntilExpiry_ThenCancelledClassIsVisible()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        var key = await KeyAsync(host, scenario.UserId, id);
        var first = await CardAsync(host, scenario.UserId, id);
        Assert.IsFalse(first.GetProperty("class").GetProperty("isCancelled").GetBoolean());
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsCancelled, true)));
        Assert.AreEqual(key, await KeyAsync(host, scenario.UserId, id));
        var cached = await CardAsync(host, scenario.UserId, id);
        Assert.IsFalse(cached.GetProperty("class").GetProperty("isCancelled").GetBoolean());
        var redis = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.KeyExpireAsync(key, TimeSpan.Zero);
        var fresh = await CardAsync(host, scenario.UserId, id);
        Assert.IsTrue(fresh.GetProperty("class").GetProperty("isCancelled").GetBoolean());
    }

    [TestMethod]
    public async Task ResizeAndCancel_UseNewVersionKeys()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        var firstKey = await KeyAsync(host, scenario.UserId, id);
        await CardAsync(host, scenario.UserId, id);
        using var changed = await host.SendAsync(scenario.UserId, $"/enrollments/{id}/seats", "{\"seats\":3}");
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        var secondKey = await KeyAsync(host, scenario.UserId, id);
        Assert.AreNotEqual(firstKey, secondKey);
        var resized = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(3, resized.GetProperty("seats").GetInt32());
        Assert.AreEqual(7, resized.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        var thirdKey = await KeyAsync(host, scenario.UserId, id);
        Assert.AreNotEqual(secondKey, thirdKey);
        var final = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual("cancelled", final.GetProperty("status").GetString());
        Assert.AreEqual(10, final.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        var redis = host.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        Assert.IsTrue(await redis.KeyExistsAsync(firstKey));
        Assert.IsTrue(await redis.KeyExistsAsync(secondKey));
        Assert.IsTrue(await redis.KeyExistsAsync(thirdKey));
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("resize")]
    [DataRow("leave")]
    public async Task Promotion_RefreshesPassInOtherCachedCards(string operation)
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 4);
        using var full = await host.PostAsync(scenario.UserId, scenario.ClassId, 4);
        var ownerBooking = await WaitlistTestData.IdAsync(full);
        var waiting = await host.AddUserAsync();
        var otherClass = await host.AddClassAsync();
        using var existing = await host.PostAsync(waiting, otherClass);
        var otherBooking = await WaitlistTestData.IdAsync(existing);
        long blocker = 0;
        long blockerEntry = 0;
        if (operation == "leave")
        {
            blocker = await host.AddUserAsync();
            using var joinedBlocker = await host.JoinAsync(blocker, scenario.ClassId, 2);
            blockerEntry = await WaitlistTestData.IdAsync(joinedBlocker);
        }
        var requested = operation == "leave" ? 1 : 2;
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, requested);
        var waitingId = await WaitlistTestData.IdAsync(joined);
        var oldKey = await KeyAsync(host, waiting, otherBooking);
        var oldCard = await CardAsync(host, waiting, otherBooking);
        Assert.AreEqual(9, oldCard.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        if (operation == "cancel")
        {
            using var response = await host.CancelBookingAsync(scenario.UserId, ownerBooking);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            var seats = operation == "leave" ? 3 : 2;
            using var response = await host.SendAsync(scenario.UserId, $"/enrollments/{ownerBooking}/seats",
                JsonSerializer.Serialize(new { seats }));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            if (operation == "leave")
            {
                using var left = await host.LeaveAsync(blocker, blockerEntry);
                Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
            }
        }
        Assert.AreEqual("promoted", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == waitingId)
            .Select(x => x.Status).SingleAsync()));
        Assert.AreNotEqual(oldKey, await KeyAsync(host, waiting, otherBooking));
        var fresh = await CardAsync(host, waiting, otherBooking);
        Assert.AreEqual(9 - requested, fresh.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    private static async Task<JsonElement> CardAsync(EnrollmentTestHost host, long userId, long id)
    {
        using var client = host.Client(userId);
        using var response = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await EnrollmentTestHost.JsonAsync(response);
    }

    private static async Task<string> KeyAsync(EnrollmentTestHost host, long userId, long id)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var versions = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().Enrollment.GetCardVersionsAsync(id, userId);
        Assert.IsNotNull(versions);
        var options = scope.ServiceProvider.GetRequiredService<EnrollmentCardCacheOptions>();
        return $"{options.KeyPrefix}:card:{userId}:{id}:{versions.EnrollmentVersion}:{versions.PassVersion}";
    }

    private sealed class CardQueries : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM enrollments", StringComparison.Ordinal)
                && command.CommandText.Contains("JOIN passes", StringComparison.Ordinal))
                Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
