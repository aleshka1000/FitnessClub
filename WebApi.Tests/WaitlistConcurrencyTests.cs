using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class WaitlistConcurrencyTests
{
    [TestMethod]
    public async Task DifferentKeysForSameUserAndClass_CreateOnlyOneWaitingEntry()
    {
        var barrier = new ActiveSeatsBarrier { Enabled = false };
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        barrier.Enabled = true;
        var responses = await Task.WhenAll(host.JoinAsync(user, scenario.ClassId, 1, "first"),
            host.JoinAsync(user, scenario.ClassId, 1, "second"));
        try
        {
            CollectionAssert.AreEquivalent(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).ToArray());
            await WaitlistTestData.AssertReasonAsync(responses.Single(x => x.StatusCode == HttpStatusCode.Conflict), "already-waiting");
            Assert.AreEqual(1, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
            Assert.AreEqual(1, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == user)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestMethod]
    public async Task ConcurrentSameKey_ReturnsSameWaitingEntry()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => host.JoinAsync(user, scenario.ClassId, 1, "key")));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.Created));
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
            Assert.AreEqual(1, bodies.Distinct().Count());
            Assert.AreEqual(1, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestMethod]
    public async Task ConcurrentCancellations_RefundAndPromoteOnlyOnce()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId, 2);
        await WaitlistTestData.IdAsync(joined);
        barrier.ClassId = scenario.ClassId;
        var responses = await Task.WhenAll(host.CancelBookingAsync(scenario.UserId, booking), host.CancelBookingAsync(scenario.UserId, booking));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.OK));
            var bodies = await Task.WhenAll(responses.Select(EnrollmentTestHost.JsonAsync));
            Assert.AreEqual(2, bodies.Sum(x => x.GetProperty("refundedVisits").GetInt32()));
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
            Assert.AreEqual(20, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).Select(x => x.RemainingVisits).SingleAsync()));
            Assert.AreEqual(8, await host.DbAsync(db => db.Passes.Where(x => x.UserId == user).Select(x => x.RemainingVisits).SingleAsync()));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestMethod]
    public async Task LeavingRacesWithPromotion_OnlyOneTransitionWins()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId, 2);
        var entryId = await WaitlistTestData.IdAsync(joined);
        barrier.ClassId = scenario.ClassId;
        var cancelled = host.CancelBookingAsync(scenario.UserId, booking);
        var leaving = host.LeaveAsync(user, entryId);
        using var cancelResponse = await cancelled;
        using var leaveResponse = await leaving;
        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);
        var entry = await host.DbAsync(db => db.WaitlistEntries.SingleAsync(x => x.Id == entryId));
        if (entry.Status == "promoted")
        {
            await WaitlistTestData.AssertReasonAsync(leaveResponse, "waitlist-promoted");
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
        }
        else
        {
            Assert.AreEqual("cancelled", entry.Status);
            Assert.AreEqual(HttpStatusCode.OK, leaveResponse.StatusCode);
            Assert.AreEqual(0, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
        }
    }

    [TestMethod]
    public async Task PromotionAndOrdinaryBooking_DoNotExceedCapacity()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync(capacity: 3);
        using var booked = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var booking = await WaitlistTestData.IdAsync(booked);
        var waiting = await host.AddUserAsync();
        var newcomer = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        await WaitlistTestData.IdAsync(joined);
        barrier.ClassId = scenario.ClassId;
        var cancelling = host.CancelBookingAsync(scenario.UserId, booking);
        var creating = host.PostAsync(newcomer, scenario.ClassId, 1);
        using var cancellation = await cancelling;
        using var creation = await creating;
        Assert.AreEqual(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, creation.StatusCode);
        Assert.AreEqual(3, await host.DbAsync(db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
    }

    [TestMethod]
    public async Task TwoClassesCompeteForSamePass_OnePromotesAndOtherSkips()
    {
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (first, firstBooking) = await WaitlistTestData.FullAsync(host);
        var (second, secondBooking) = await WaitlistTestData.FullAsync(host);
        var waiting = await host.AddUserAsync(2);
        using var joinedFirst = await host.JoinAsync(waiting, first.ClassId, 2);
        await WaitlistTestData.IdAsync(joinedFirst);
        using var joinedSecond = await host.JoinAsync(waiting, second.ClassId, 2);
        await WaitlistTestData.IdAsync(joinedSecond);
        barrier.AddedEnrollmentUserId = waiting;
        var responses = await Task.WhenAll(host.CancelBookingAsync(first.UserId, firstBooking), host.CancelBookingAsync(second.UserId, secondBooking));
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.OK));
            var statuses = await host.DbAsync(db => db.WaitlistEntries.Where(x => x.UserId == waiting).Select(x => x.Status).ToListAsync());
            CollectionAssert.AreEquivalent(new[] { "promoted", "skipped" }, statuses);
            Assert.AreEqual(0, await host.DbAsync(db => db.Passes.Where(x => x.UserId == waiting).Select(x => x.RemainingVisits).SingleAsync()));
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == waiting)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [TestMethod]
    public async Task FailedPromotion_RollsBackCancellationRefundAndQueue_AndCanBeRetried()
    {
        var failures = new ForcedConcurrencyFailures(5);
        await using var host = new EnrollmentTestHost(failures);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var waiting = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        var entryId = await WaitlistTestData.IdAsync(joined);
        var before = await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync());
        failures.UserId = waiting;
        using var response = await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await host.DbAsync(async db =>
        {
            Assert.AreEqual("active", (await db.Enrollments.SingleAsync(x => x.Id == booking)).Status);
            Assert.AreEqual(18, (await db.Passes.SingleAsync(x => x.UserId == scenario.UserId)).RemainingVisits);
            Assert.AreEqual(10, (await db.Passes.SingleAsync(x => x.UserId == waiting)).RemainingVisits);
            Assert.AreEqual(before, (await db.Classes.SingleAsync(x => x.Id == scenario.ClassId)).Version);
            var entry = await db.WaitlistEntries.SingleAsync(x => x.Id == entryId);
            Assert.AreEqual("waiting", entry.Status);
            Assert.IsNull(entry.PromotedEnrollmentId);
            Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == waiting));
            return true;
        });
        using var recovered = await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == waiting)));
    }
}
