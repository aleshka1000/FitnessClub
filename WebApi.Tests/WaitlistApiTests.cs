using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class WaitlistApiTests
{
    [TestMethod]
    public async Task Join_HasExactContract_AndDoesNotCheckOrChargePass()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync(0, new DateOnly(2029, 1, 1));
        var before = await host.DbAsync(db => db.Passes.SingleAsync(x => x.UserId == user));
        using var response = await host.JoinAsync(user, scenario.ClassId, 2, "queue-key");
        var id = await WaitlistTestData.IdAsync(response);
        var body = await EnrollmentTestHost.JsonAsync(response);
        CollectionAssert.AreEquivalent(new[] { "id", "classId", "seats", "position", "createdAt" },
            body.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(1, body.GetProperty("position").GetInt32());
        await host.DbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == user);
            Assert.AreEqual(before.Version, pass.Version);
            Assert.AreEqual(0, pass.RemainingVisits);
            var record = await db.IdempotencyRequests.SingleAsync(x => x.UserId == user);
            Assert.AreEqual(id, record.WaitlistEntryId);
            Assert.IsNull(record.EnrollmentId);
            Assert.AreEqual("waiting", (await db.WaitlistEntries.SingleAsync(x => x.Id == id)).Status);
            return true;
        });
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("{\"seats\":1}")]
    [DataRow("{\"classId\":1}")]
    [DataRow("{\"classId\":null,\"seats\":1}")]
    [DataRow("{\"classId\":\"1\",\"seats\":1}")]
    [DataRow("{\"classId\":1,\"seats\":0}")]
    [DataRow("{\"classId\":1,\"seats\":-1}")]
    [DataRow("{\"classId\":1,\"seats\":2147483648}")]
    public async Task InvalidJoinBody_Returns400(string? body)
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var response = await host.SendAsync(user, "/enrollments/waitlist", body, "key");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
        Assert.AreEqual(0, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == user)));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task MissingOrBlankKey_Returns400(string? key)
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var response = await host.SendAsync(user, "/enrollments/waitlist", "{\"classId\":1,\"seats\":1}", key);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("capacity-exceeded")]
    [DataRow("already-waiting")]
    public async Task JoinBusinessErrors_LeaveNoNewEntryOrKey(string reason)
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        if (reason == "class-cancelled")
            await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsCancelled, true)));
        if (reason == "class-started")
            await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.StartsAt, host.Clock.Now.UtcDateTime)));
        if (reason == "already-waiting")
        {
            using var initial = await host.JoinAsync(user, scenario.ClassId, 1, "initial");
            await WaitlistTestData.IdAsync(initial);
        }
        using var response = await host.JoinAsync(user, scenario.ClassId, reason == "capacity-exceeded" ? 3 : 1, "failed");
        await WaitlistTestData.AssertReasonAsync(response, reason);
        Assert.AreEqual(reason == "already-waiting" ? 1 : 0,
            await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
        Assert.AreEqual(0, await host.DbAsync(db => db.IdempotencyRequests
            .CountAsync(x => x.UserId == user && x.OriginalKey == "failed")));
    }

    [TestMethod]
    public async Task AvailableSeats_Return409_AndMissingClassReturns404()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var available = await host.JoinAsync(scenario.UserId, scenario.ClassId, 2);
        await WaitlistTestData.AssertReasonAsync(available, "seats-available");
        using var missing = await host.JoinAsync(scenario.UserId, long.MaxValue);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    public async Task Replay_ReturnsCurrentPosition_WithEqualTimestampsOrderedById()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var firstUser = await host.AddUserAsync();
        var secondUser = await host.AddUserAsync();
        var thirdUser = await host.AddUserAsync();
        using var first = await host.JoinAsync(firstUser, scenario.ClassId, 1, "first");
        var firstId = await WaitlistTestData.IdAsync(first);
        using var second = await host.JoinAsync(secondUser, scenario.ClassId, 1, "second");
        await WaitlistTestData.IdAsync(second);
        using var third = await host.JoinAsync(thirdUser, scenario.ClassId, 1, "third");
        var thirdId = await WaitlistTestData.IdAsync(third);
        Assert.AreEqual(3, (await EnrollmentTestHost.JsonAsync(third)).GetProperty("position").GetInt32());
        using var left = await host.LeaveAsync(firstUser, firstId);
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
        using var replay = await host.JoinAsync(thirdUser, scenario.ClassId, 1, "third");
        Assert.AreEqual(HttpStatusCode.Created, replay.StatusCode);
        var body = await EnrollmentTestHost.JsonAsync(replay);
        Assert.AreEqual(thirdId, body.GetProperty("id").GetInt64());
        Assert.AreEqual(2, body.GetProperty("position").GetInt32());
        using var changed = await host.JoinAsync(thirdUser, scenario.ClassId, 2, "third");
        await WaitlistTestData.AssertReasonAsync(changed, "idempotency-key-reused");
    }

    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("skipped")]
    [DataRow("promoted")]
    public async Task InactiveReplay_Return409_AndHistoryRemains(string status)
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync(status == "skipped" ? 0 : 10);
        using var joined = await host.JoinAsync(user, scenario.ClassId, 1, "key");
        var id = await WaitlistTestData.IdAsync(joined);
        using var finished = status == "cancelled" ? await host.LeaveAsync(user, id)
            : await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.OK, finished.StatusCode);
        using var replay = await host.JoinAsync(user, scenario.ClassId, 1, "key");
        await WaitlistTestData.AssertReasonAsync(replay, "waitlist-inactive");
        Assert.AreEqual(status, await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == id)
            .Select(x => x.Status).SingleAsync()));
        using var left = await host.LeaveAsync(user, id);
        if (status == "promoted") await WaitlistTestData.AssertReasonAsync(left, "waitlist-promoted");
        else Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
        Assert.AreEqual(status, await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == id)
            .Select(x => x.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task ActiveBookingAndSameKeyInDifferentOperation_DoNotPreventWaiting()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        using var joined = await host.JoinAsync(scenario.UserId, scenario.ClassId, 1, "booking-key");
        await WaitlistTestData.IdAsync(joined);
        Assert.AreEqual(2, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == scenario.UserId)));
    }

    [TestMethod]
    public async Task UnknownFieldsAreIgnored_AndAnotherUserCannotLeave()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.SendAsync(user, "/enrollments/waitlist",
            JsonSerializer.Serialize(new { classId = scenario.ClassId, seats = 1, userId = scenario.UserId, unknown = true }), "key");
        var id = await WaitlistTestData.IdAsync(joined);
        using var foreign = await host.LeaveAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.AreEqual("waiting", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == id)
            .Select(x => x.Status).SingleAsync()));
    }

    [TestMethod]
    [DataRow("/enrollments/waitlist/not-a-number/cancel", 400)]
    [DataRow("/enrollments/not-a-number/cancel", 400)]
    [DataRow("/enrollments/waitlist/-1/cancel", 404)]
    [DataRow("/enrollments/-1/cancel", 404)]
    public async Task InvalidOrMissingCancellationId_ReturnsExpectedStatus(string path, int status)
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var response = await host.SendAsync(user, path);
        Assert.AreEqual(status, (int)response.StatusCode);
    }

    [TestMethod]
    [DataRow("/enrollments/waitlist")]
    [DataRow("/enrollments/waitlist/1/cancel")]
    [DataRow("/enrollments/1/cancel")]
    public async Task NewRoutes_RequireAuthentication(string path)
    {
        await using var host = new EnrollmentTestHost();
        using var response = await host.SendAsync(null, path);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task CommonLimit_CountsAllNewRoutes()
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        for (var i = 0; i < 60; i++)
        {
            using var response = (i % 3) switch
            {
                0 => await host.CancelBookingAsync(user, -1),
                1 => await host.LeaveAsync(user, -1),
                _ => await host.JoinAsync(user, -1)
            };
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var limited = await host.PostAsync(user, -1);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
