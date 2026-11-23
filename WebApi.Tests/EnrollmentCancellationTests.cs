using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentCancellationTests
{
    [TestMethod]
    [DataRow(120, false, 2)]
    [DataRow(119, false, 0)]
    [DataRow(1, false, 0)]
    [DataRow(0, true, 2)]
    [DataRow(-60, true, 2)]
    public async Task Cancellation_UsesTimeBoundaryAndClubCancellation(int minutes, bool clubCancelled, int refunded)
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.StartsAt, host.Clock.Now.UtcDateTime.AddMinutes(minutes))
                .SetProperty(x => x.IsCancelled, clubCancelled)));
        using var response = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await EnrollmentTestHost.JsonAsync(response);
        CollectionAssert.AreEquivalent(new[] { "id", "status", "refundedVisits" }, body.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(id, body.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", body.GetProperty("status").GetString());
        Assert.AreEqual(refunded, body.GetProperty("refundedVisits").GetInt32());
        using var repeated = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(0, (await EnrollmentTestHost.JsonAsync(repeated)).GetProperty("refundedVisits").GetInt32());
        await host.DbAsync(async db =>
        {
            Assert.AreEqual(18 + refunded, (await db.Passes.SingleAsync(x => x.UserId == scenario.UserId)).RemainingVisits);
            var enrollment = await db.Enrollments.SingleAsync(x => x.Id == id);
            Assert.AreEqual(2, enrollment.Seats);
            Assert.IsNotNull(enrollment.CancelledAt);
            return true;
        });
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task StartedClass_CannotCancelActiveBooking(int minutes)
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.StartsAt, host.Clock.Now.UtcDateTime.AddMinutes(minutes))));
        using var response = await host.CancelBookingAsync(scenario.UserId, id);
        await WaitlistTestData.AssertReasonAsync(response, "class-started");
        Assert.AreEqual("active", await host.DbAsync(db => db.Enrollments.Where(x => x.Id == id).Select(x => x.Status).SingleAsync()));
        Assert.AreEqual(18, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    public async Task ForeignBooking_IsHidden_AndExpiredPassCanReceiveRefund()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        var other = await host.AddUserAsync();
        using var foreign = await host.CancelBookingAsync(other, id);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
        await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ValidUntil, new DateOnly(2029, 1, 1))));
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(20, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    public async Task CancelledClass_DoesNotPromoteWaitlist_AndLeavingIsStillAllowed()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId);
        var waitingId = await WaitlistTestData.IdAsync(joined);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsCancelled, true)));
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual("waiting", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == waitingId).Select(x => x.Status).SingleAsync()));
        using var left = await host.LeaveAsync(user, waitingId);
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
        Assert.AreEqual(0, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
    }
}
