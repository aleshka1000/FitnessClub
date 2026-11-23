using System.Net;
using BusinessLogicLayer.Services;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using DataAccessLayer.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace WebApi.Tests;

[TestClass]
public class WaitlistPromotionTests
{
    [TestMethod]
    public async Task NotEnoughSeats_DoesNotReadPass()
    {
        var unit = new Mock<IUnitOfWork>(MockBehavior.Strict);
        var entries = new List<WaitlistEntry> { new() { Id = 1, ClassId = 1, UserId = 1, Seats = 3 } };
        unit.Setup(x => x.Enrollment.GetActiveSeatsAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(1L);
        unit.Setup(x => x.Waitlist.GetWaitingBatchAsync(1, null, 0, It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        unit.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var service = new WaitlistPromotionService(unit.Object);
        var now = DateTime.UtcNow;
        await service.PromoteAsync(new Class { Id = 1, Capacity = 3, StartsAt = now.AddDays(1) }, now);
        unit.VerifyGet(x => x.Pass, Times.Never);
        Assert.AreEqual("waiting", entries[0].Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeadRequestBlocksSmallerRequest_UntilEnoughSeatsEvenWithExpiredPass(bool expired)
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 3, visits: 20);
        using var bookingTwo = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var twoId = await WaitlistTestData.IdAsync(bookingTwo);
        using var bookingOne = await host.PostAsync(scenario.UserId, scenario.ClassId, 1);
        var oneId = await WaitlistTestData.IdAsync(bookingOne);
        var head = await host.AddUserAsync(10, expired ? new DateOnly(2029, 1, 1) : null);
        var next = await host.AddUserAsync();
        using var joinedHead = await host.JoinAsync(head, scenario.ClassId, 3);
        var headId = await WaitlistTestData.IdAsync(joinedHead);
        using var joinedNext = await host.JoinAsync(next, scenario.ClassId, 1);
        var nextId = await WaitlistTestData.IdAsync(joinedNext);
        using var cancelledTwo = await host.CancelBookingAsync(scenario.UserId, twoId);
        Assert.AreEqual(HttpStatusCode.OK, cancelledTwo.StatusCode);
        Assert.AreEqual(2, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == scenario.ClassId && x.Status == "waiting")));
        using var cancelledOne = await host.CancelBookingAsync(scenario.UserId, oneId);
        Assert.AreEqual(HttpStatusCode.OK, cancelledOne.StatusCode);
        await host.DbAsync(async db =>
        {
            Assert.AreEqual(expired ? "skipped" : "promoted", (await db.WaitlistEntries.SingleAsync(x => x.Id == headId)).Status);
            Assert.AreEqual(expired ? "promoted" : "waiting", (await db.WaitlistEntries.SingleAsync(x => x.Id == nextId)).Status);
            Assert.AreEqual(expired ? 1 : 3, await db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active")
                .SumAsync(x => x.Seats));
            return true;
        });
    }

    [TestMethod]
    public async Task LeavingBlockedHead_PromotesNextUsingAlreadyFreeSeats()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 3);
        using var two = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var twoId = await WaitlistTestData.IdAsync(two);
        using var one = await host.PostAsync(scenario.UserId, scenario.ClassId, 1);
        await WaitlistTestData.IdAsync(one);
        var head = await host.AddUserAsync();
        var next = await host.AddUserAsync();
        using var joinedHead = await host.JoinAsync(head, scenario.ClassId, 3);
        var headId = await WaitlistTestData.IdAsync(joinedHead);
        using var joinedNext = await host.JoinAsync(next, scenario.ClassId, 1);
        var nextId = await WaitlistTestData.IdAsync(joinedNext);
        using var freed = await host.CancelBookingAsync(scenario.UserId, twoId);
        using var left = await host.LeaveAsync(head, headId);
        Assert.AreEqual(HttpStatusCode.OK, left.StatusCode);
        var promoted = await host.DbAsync(db => db.WaitlistEntries.SingleAsync(x => x.Id == nextId));
        Assert.AreEqual("promoted", promoted.Status);
        Assert.IsNotNull(promoted.PromotedEnrollmentId);
        using var repeated = await host.LeaveAsync(head, headId);
        Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
        Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == next)));
    }

    [TestMethod]
    [DataRow("pass-expired")]
    [DataRow("pass-exhausted")]
    public async Task IneligibleHead_IsRecordedAsSkipped_AndNextReceivesBooking(string reason)
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var head = await host.AddUserAsync(reason == "pass-exhausted" ? 0 : 10,
            reason == "pass-expired" ? new DateOnly(2029, 1, 1) : null);
        var next = await host.AddUserAsync();
        using var first = await host.JoinAsync(head, scenario.ClassId, 1);
        var firstId = await WaitlistTestData.IdAsync(first);
        using var second = await host.JoinAsync(next, scenario.ClassId, 2);
        var secondId = await WaitlistTestData.IdAsync(second);
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        await host.DbAsync(async db =>
        {
            var skipped = await db.WaitlistEntries.SingleAsync(x => x.Id == firstId);
            Assert.AreEqual("skipped", skipped.Status);
            Assert.AreEqual(reason, skipped.SkipReason);
            Assert.IsNotNull(skipped.FinishedAt);
            Assert.IsNull(skipped.PromotedEnrollmentId);
            var promoted = await db.WaitlistEntries.SingleAsync(x => x.Id == secondId);
            Assert.AreEqual("promoted", promoted.Status);
            Assert.AreEqual(8, (await db.Passes.SingleAsync(x => x.UserId == next)).RemainingVisits);
            Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == head));
            return true;
        });
    }

    [TestMethod]
    public async Task MoreThanOneBatch_IsProcessedInOrder()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, booking) = await WaitlistTestData.FullAsync(host, capacity: 1);
        for (var i = 0; i < 33; i++)
        {
            var user = await host.AddUserAsync(0);
            using var joined = await host.JoinAsync(user, scenario.ClassId);
            await WaitlistTestData.IdAsync(joined);
        }
        var last = await host.AddUserAsync();
        using var lastJoined = await host.JoinAsync(last, scenario.ClassId);
        var lastId = await WaitlistTestData.IdAsync(lastJoined);
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(33, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.ClassId == scenario.ClassId && x.Status == "skipped")));
        Assert.AreEqual("promoted", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == lastId).Select(x => x.Status).SingleAsync()));
    }

    [TestMethod]
    public async Task RefundedVisits_CanImmediatelyFundSameUsersWaitlistBooking()
    {
        await using var host = new EnrollmentTestHost();
        var (scenario, booking) = await WaitlistTestData.FullAsync(host, visits: 2);
        using var joined = await host.JoinAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await WaitlistTestData.IdAsync(joined);
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, booking);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(2, (await EnrollmentTestHost.JsonAsync(cancelled)).GetProperty("refundedVisits").GetInt32());
        Assert.AreEqual("promoted", await host.DbAsync(db => db.WaitlistEntries.Where(x => x.Id == id).Select(x => x.Status).SingleAsync()));
        Assert.AreEqual(0, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).Select(x => x.RemainingVisits).SingleAsync()));
    }
}
