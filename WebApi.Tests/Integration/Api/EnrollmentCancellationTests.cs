using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentCancellationTests
{
    [TestMethod]
    public async Task ExpiredPass_ReceivesFullRefund()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ValidUntil, new DateOnly(2029, 1, 1))));

        // Act
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(2, (await ApiAssertions.JsonAsync(cancelled)).GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual(20, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                    .Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    [DataRow(120, false, 2)]
    [DataRow(119, false, 0)]
    [DataRow(1, false, 0)]
    [DataRow(0, true, 2)]
    [DataRow(-60, true, 2)]
    public async Task Cancellation_UsesTimeBoundaryAndClubCancellation(int minutes, bool clubCancelled, int refunded)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    x => x.StartsAt,
                    host.Clock.Now.UtcDateTime.AddMinutes(minutes))
                    .SetProperty(x => x.IsCancelled, clubCancelled)));

        // Act
        using var response = await host.CancelBookingAsync(scenario.UserId, id);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiAssertions.JsonAsync(response);
        CollectionAssert.AreEquivalent(
            new[] { "id", "status", "refundedVisits" },
            body.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(id, body.GetProperty("id").GetInt64());
        Assert.AreEqual("cancelled", body.GetProperty("status").GetString());
        Assert.AreEqual(refunded, body.GetProperty("refundedVisits").GetInt32());

        // Act
        using var repeated = await host.CancelBookingAsync(scenario.UserId, id);

        // Assert
        Assert.AreEqual(0, (await ApiAssertions.JsonAsync(repeated)).GetProperty("refundedVisits").GetInt32());
        await host.DbAsync(async db =>
        {
            Assert.AreEqual(
                18 + refunded,
                (await db.Passes.SingleAsync(x => x.UserId == scenario.UserId)).RemainingVisits);
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
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(
                update => update.SetProperty(
                    x => x.StartsAt,
                    host.Clock.Now.UtcDateTime.AddMinutes(minutes))));
        var before = await host.GetClassStateAsync(scenario.ClassId);

        // Act
        using var response = await host.CancelBookingAsync(scenario.UserId, id);

        // Assert
        await ApiAssertions.ConflictAsync(response, "class-started");
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId));
    }

    [TestMethod]
    public async Task CancelledClass_DoesNotPromoteWaitlist_AndLeavingIsStillAllowed()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, id) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId);
        var waitingId = await ApiAssertions.CreatedIdAsync(joined);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsCancelled, true)));

        // Act
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(
            "waiting",
            await host.DbAsync(
                db => db.WaitlistEntries.Where(x => x.Id == waitingId).Select(x => x.Status).SingleAsync()));

        // Act
        using var left = await host.LeaveAsync(user, waitingId);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
        Assert.AreEqual(0, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
    }
}
