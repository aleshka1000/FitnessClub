using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentDetailsAndSeatsTests
{
    private static Task<HttpResponseMessage> ChangeAsync(EnrollmentTestHost host, long user, long id, int seats) =>
        host.SendAsync(user, $"/enrollments/{id}/seats", JsonSerializer.Serialize(new { seats }));

    private static async Task<JsonElement> CardAsync(EnrollmentTestHost host, long user, long id)
    {
        using var client = host.Client(user);
        using var response = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await EnrollmentTestHost.JsonAsync(response);
    }

    [TestMethod]
    public async Task Card_ReturnsExactFields_AndCurrentPassAcrossBookings()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await WaitlistTestData.IdAsync(created);
        var card = await CardAsync(host, scenario.UserId, id);
        CollectionAssert.AreEquivalent(new[] { "id", "status", "seats", "createdAt", "class", "pass" },
            card.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "id", "title", "startsAt", "durationMinutes", "isCancelled", "room" },
            card.GetProperty("class").EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "id", "name" },
            card.GetProperty("class").GetProperty("room").EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "remainingVisits", "validUntil" },
            card.GetProperty("pass").EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(8, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("2030-02-01", card.GetProperty("pass").GetProperty("validUntil").GetString());

        using var second = await host.PostAsync(scenario.UserId, scenario.ClassId);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        card = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(7, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);
        card = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
        Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(9, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsCancelled, true)));
        card = await CardAsync(host, scenario.UserId, id);
        Assert.IsTrue(card.GetProperty("class").GetProperty("isCancelled").GetBoolean());
    }

    [TestMethod]
    public async Task MissingForeignOrMalformedId_AndAuthentication_AreHandled()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        var other = await host.AddUserAsync();
        using var foreign = host.Client(other);
        using var owner = host.Client(scenario.UserId);
        using var anonymous = host.Client();
        foreach (var target in new[] { id, long.MaxValue })
        {
            using var get = await foreign.GetAsync($"/enrollments/{target}");
            Assert.AreEqual(HttpStatusCode.NotFound, get.StatusCode);
            using var change = await ChangeAsync(host, other, target, 2);
            Assert.AreEqual(HttpStatusCode.NotFound, change.StatusCode);
        }
        using var malformed = await owner.GetAsync("/enrollments/no-number");
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var noToken = await anonymous.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, noToken.StatusCode);
        using var anonymousChange = await host.SendAsync(null, $"/enrollments/{id}/seats", "{\"seats\":2}");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousChange.StatusCode);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("{}")]
    [DataRow("{\"seats\":null}")]
    [DataRow("{\"seats\":0}")]
    [DataRow("{\"seats\":-1}")]
    [DataRow("{\"seats\":\"2\"}")]
    [DataRow("{\"seats\":1.5}")]
    public async Task InvalidSeats_Return400(string? json)
    {
        await using var host = new EnrollmentTestHost();
        using var response = await host.SendAsync(1, "/enrollments/1/seats", json);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task IncreaseAndDecrease_UseDifference_AndReturnFullCard()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(startsIn: TimeSpan.FromMinutes(30));
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await WaitlistTestData.IdAsync(created);
        using var increased = await host.SendAsync(scenario.UserId, $"/enrollments/{id}/seats",
            "{\"seats\":4,\"ignored\":true}");
        Assert.AreEqual(HttpStatusCode.OK, increased.StatusCode);
        var card = await EnrollmentTestHost.JsonAsync(increased);
        Assert.AreEqual(4, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(scenario.RoomId, card.GetProperty("class").GetProperty("room").GetProperty("id").GetInt64());

        await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.ValidUntil, new DateOnly(2029, 1, 1)).SetProperty(x => x.RemainingVisits, 0)));
        using var decreased = await ChangeAsync(host, scenario.UserId, id, 1);
        Assert.AreEqual(HttpStatusCode.OK, decreased.StatusCode);
        card = await EnrollmentTestHost.JsonAsync(decreased);
        Assert.AreEqual(1, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(3, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        var before = await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync());
        using var unchanged = await ChangeAsync(host, scenario.UserId, id, 1);
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.AreEqual(before, await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync()));
        Assert.AreEqual((await CardAsync(host, scenario.UserId, id)).GetRawText(),
            (await EnrollmentTestHost.JsonAsync(unchanged)).GetRawText());
    }

    [TestMethod]
    [DataRow("enrollment-cancelled", 1)]
    [DataRow("enrollment-cancelled", 2)]
    [DataRow("class-cancelled", 1)]
    [DataRow("class-cancelled", 2)]
    [DataRow("class-started", 1)]
    [DataRow("class-started", 2)]
    [DataRow("pass-expired", 2)]
    [DataRow("pass-exhausted", 2)]
    [DataRow("capacity-exceeded", 2)]
    public async Task Refusals_LeaveStateUnchanged(string reason, int seats)
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 1);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        await host.DbAsync(async db =>
        {
            var booking = await db.Enrollments.SingleAsync(x => x.Id == id);
            var scheduledClass = await db.Classes.SingleAsync(x => x.Id == scenario.ClassId);
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);
            if (reason == "enrollment-cancelled") booking.Status = "cancelled";
            if (reason == "class-cancelled") scheduledClass.IsCancelled = true;
            if (reason == "class-started") scheduledClass.StartsAt = host.Clock.Now.UtcDateTime;
            if (reason == "pass-expired") pass.ValidUntil = new DateOnly(2029, 1, 1);
            if (reason == "pass-exhausted") { pass.RemainingVisits = 0; scheduledClass.Capacity = 4; }
            await db.SaveChangesAsync();
            return true;
        });
        var before = await CardAsync(host, scenario.UserId, id);
        using var response = await ChangeAsync(host, scenario.UserId, id, seats);
        await WaitlistTestData.AssertReasonAsync(response, reason);
        Assert.AreEqual(before.GetRawText(), (await CardAsync(host, scenario.UserId, id)).GetRawText());
    }

    [TestMethod]
    public async Task Decrease_PromotesOwnWaitlist_AndReturnsFinalPassBalance()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 4);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 4);
        var id = await WaitlistTestData.IdAsync(created);
        using var joined = await host.JoinAsync(scenario.UserId, scenario.ClassId, 2);
        var waitingId = await WaitlistTestData.IdAsync(joined);
        using var changed = await ChangeAsync(host, scenario.UserId, id, 2);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        var card = await EnrollmentTestHost.JsonAsync(changed);
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("promoted", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == waitingId).Select(x => x.Status).SingleAsync()));
        Assert.AreEqual(4, await host.DbAsync(db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
    }

    [TestMethod]
    public async Task FailedPromotion_RollsBackSeatChangeAndRefund()
    {
        var failures = new ForcedConcurrencyFailures(5);
        await using var host = new EnrollmentTestHost(failures);
        var scenario = await host.ScenarioAsync(capacity: 4);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 4);
        var id = await WaitlistTestData.IdAsync(created);
        var waiting = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        var waitingId = await WaitlistTestData.IdAsync(joined);
        failures.UserId = waiting;
        using var response = await ChangeAsync(host, scenario.UserId, id, 2);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var card = await CardAsync(host, scenario.UserId, id);
        Assert.AreEqual(4, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual("waiting", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == waitingId).Select(x => x.Status).SingleAsync()));
        using var retry = await ChangeAsync(host, scenario.UserId, id, 2);
        Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode);
    }

    [TestMethod]
    public async Task IncreaseRacesWithBooking_OnlyOneTakesLastSeat()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync(capacity: 2);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        var other = await host.AddUserAsync();
        barrier.ClassId = scenario.ClassId;
        var responses = await Task.WhenAll(ChangeAsync(host, scenario.UserId, id, 2), host.PostAsync(other, scenario.ClassId));
        try
        {
            Assert.AreEqual(1, responses.Count(x => x.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created));
            await WaitlistTestData.AssertReasonAsync(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict), "capacity-exceeded");
            Assert.AreEqual(2, await host.DbAsync(db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestMethod]
    public async Task IncreaseRacesWithCancellation_VisitsAreAccountedOnce()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);
        barrier.ClassId = scenario.ClassId;
        var responses = await Task.WhenAll(ChangeAsync(host, scenario.UserId, id, 2), host.CancelBookingAsync(scenario.UserId, id));
        try
        {
            Assert.AreEqual(HttpStatusCode.OK, responses[1].StatusCode);
            Assert.IsTrue(responses[0].StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            if (responses[0].StatusCode == HttpStatusCode.Conflict)
                await WaitlistTestData.AssertReasonAsync(responses[0], "enrollment-cancelled");
            var card = await CardAsync(host, scenario.UserId, id);
            Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
            Assert.AreEqual(10, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
