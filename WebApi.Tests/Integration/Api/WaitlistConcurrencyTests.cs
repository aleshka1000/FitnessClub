using System.Net;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class WaitlistConcurrencyTests
{
    [TestMethod]
    public async Task JoiningAfterCancellationRead_NoWaitingEntryIsStrandedWithFreeSeats()
    {
        // Arrange
        var pause = new PauseWaitlistSave();
        await using var host = new EnrollmentTestHost(pause);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        pause.UserId = user;

        // Act
        var joining = host.JoinAsync(user, scenario.ClassId, 1, "join-during-cancellation");

        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var cancelled = await host.CancelBookingAsync(scenario.UserId, booking);
            Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        }
        finally
        {
            pause.Release();
        }

        using var response = await joining;

        // Assert
        await ApiAssertions.ConflictAsync(response, "seats-available");
        Assert.AreEqual(0, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
        Assert.AreEqual(0, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == user)));
        Assert.AreEqual(
            10,
            await host.DbAsync(
                db => db.Passes.Where(x => x.UserId == user).Select(x => x.RemainingVisits).SingleAsync()));
        Assert.AreEqual(
            0,
            await host.DbAsync(
                db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FiveConflicts_Return503AndRollbackWholeWaitlistOperation(bool leaving)
    {
        // Arrange
        var failures = new ForcedWaitlistConcurrencyFailures(leaving ? EntityState.Modified : EntityState.Added);
        await using var host = new EnrollmentTestHost(failures);
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        long waitingId = 0;

        if (leaving)
        {
            using var joined = await host.JoinAsync(user, scenario.ClassId);
            waitingId = await ApiAssertions.CreatedIdAsync(joined);
        }

        var before = await host.GetClassStateAsync(scenario.ClassId, user);
        failures.UserId = user;

        // Act
        using var failed = leaving ? await host.LeaveAsync(user, waitingId)
            : await host.JoinAsync(user, scenario.ClassId, 1, "retry-after-conflict");

        // Assert
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.AreEqual(503, (await ApiAssertions.JsonAsync(failed)).GetProperty("status").GetInt32());
        Assert.AreEqual(5, failures.Attempts);
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId, user));

        // Act
        using var retried = leaving ? await host.LeaveAsync(user, waitingId)
            : await host.JoinAsync(user, scenario.ClassId, 1, "retry-after-conflict");

        // Assert
        Assert.AreEqual(leaving ? HttpStatusCode.OK : HttpStatusCode.Created, retried.StatusCode);
        Assert.AreEqual(6, failures.Attempts);
    }

    [TestMethod]
    public async Task DifferentKeysForSameUserAndClass_CreateOnlyOneWaitingEntry()
    {
        // Arrange
        var barrier = new ActiveSeatsBarrier { Enabled = false };
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        barrier.Enabled = true;

        // Act
        var responses = await Task.WhenAll(host.JoinAsync(user, scenario.ClassId, 1, "first"),
            host.JoinAsync(user, scenario.ClassId, 1, "second"));

        // Assert
        try
        {
            CollectionAssert.AreEquivalent(
                new[] { HttpStatusCode.Created, HttpStatusCode.Conflict },
                responses.Select(x => x.StatusCode).ToArray());
            await ApiAssertions.ConflictAsync(
                responses.Single(x => x.StatusCode == HttpStatusCode.Conflict),
                "already-waiting");
            Assert.AreEqual(1, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
            Assert.AreEqual(1, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == user)));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [TestMethod]
    public async Task ConcurrentSameKey_ReturnsSameWaitingEntry()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var (scenario, _) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();

        // Act
        var responses = await Task.WhenAll(
            Enumerable.Range(
                0,
                10).Select(_ => host.JoinAsync(
                    user,
                    scenario.ClassId,
                    1,
                    "key")));

        // Assert
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.Created));
            var bodies = await Task.WhenAll(responses.Select(x => x.Content.ReadAsStringAsync()));
            Assert.AreEqual(1, bodies.Distinct().Count());
            Assert.AreEqual(1, await host.DbAsync(db => db.WaitlistEntries.CountAsync(x => x.UserId == user)));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [TestMethod]
    public async Task ConcurrentCancellations_RefundAndPromoteOnlyOnce()
    {
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId, 2);
        await ApiAssertions.CreatedIdAsync(joined);
        barrier.ClassId = scenario.ClassId;

        // Act
        var responses = await Task.WhenAll(
            host.CancelBookingAsync(
                scenario.UserId,
                booking),
            host.CancelBookingAsync(
                scenario.UserId,
                booking));

        // Assert
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.OK));
            var bodies = await Task.WhenAll(responses.Select(ApiAssertions.JsonAsync));
            Assert.AreEqual(2, bodies.Sum(x => x.GetProperty("refundedVisits").GetInt32()));
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == user)));
            Assert.AreEqual(
                20,
                await host.DbAsync(
                    db => db.Passes.Where(x => x.UserId == scenario.UserId).Select(x => x.RemainingVisits).SingleAsync()));
            Assert.AreEqual(
                8,
                await host.DbAsync(
                    db => db.Passes.Where(x => x.UserId == user).Select(x => x.RemainingVisits).SingleAsync()));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [TestMethod]
    public async Task LeavingRacesWithPromotion_OnlyOneTransitionWins()
    {
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var user = await host.AddUserAsync();
        using var joined = await host.JoinAsync(user, scenario.ClassId, 2);
        var entryId = await ApiAssertions.CreatedIdAsync(joined);
        barrier.ClassId = scenario.ClassId;

        // Act
        var cancelled = host.CancelBookingAsync(scenario.UserId, booking);
        var leaving = host.LeaveAsync(user, entryId);
        using var cancelResponse = await cancelled;
        using var leaveResponse = await leaving;

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, cancelResponse.StatusCode);
        var entry = await host.DbAsync(db => db.WaitlistEntries.SingleAsync(x => x.Id == entryId));

        if (entry.Status == "promoted")
        {
            await ApiAssertions.ConflictAsync(leaveResponse, "waitlist-promoted");
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
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync(capacity: 3);
        using var booked = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var booking = await ApiAssertions.CreatedIdAsync(booked);
        var waiting = await host.AddUserAsync();
        var newcomer = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        await ApiAssertions.CreatedIdAsync(joined);
        barrier.ClassId = scenario.ClassId;

        // Act
        var cancelling = host.CancelBookingAsync(scenario.UserId, booking);
        var creating = host.PostAsync(newcomer, scenario.ClassId, 1);
        using var cancellation = await cancelling;
        using var creation = await creating;

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, creation.StatusCode);
        Assert.AreEqual(
            3,
            await host.DbAsync(
                db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
    }

    [TestMethod]
    public async Task TwoClassesCompeteForSamePass_OnePromotesAndOtherSkips()
    {
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var (first, firstBooking) = await WaitlistTestData.FullAsync(host);
        var (second, secondBooking) = await WaitlistTestData.FullAsync(host);
        var waiting = await host.AddUserAsync(2);
        using var joinedFirst = await host.JoinAsync(waiting, first.ClassId, 2);
        await ApiAssertions.CreatedIdAsync(joinedFirst);
        using var joinedSecond = await host.JoinAsync(waiting, second.ClassId, 2);
        await ApiAssertions.CreatedIdAsync(joinedSecond);
        barrier.AddedEnrollmentUserId = waiting;

        // Act
        var responses = await Task.WhenAll(
            host.CancelBookingAsync(
                first.UserId,
                firstBooking),
            host.CancelBookingAsync(
                second.UserId,
                secondBooking));

        // Assert
        try
        {
            Assert.IsTrue(responses.All(x => x.StatusCode == HttpStatusCode.OK));
            var statuses = await host.DbAsync(
                db => db.WaitlistEntries.Where(x => x.UserId == waiting).Select(x => x.Status).ToListAsync());
            CollectionAssert.AreEquivalent(new[] { "promoted", "skipped" }, statuses);
            Assert.AreEqual(
                0,
                await host.DbAsync(
                    db => db.Passes.Where(x => x.UserId == waiting).Select(x => x.RemainingVisits).SingleAsync()));
            Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == waiting)));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [TestMethod]
    public async Task FailedPromotion_RollsBackCancellationRefundAndQueue_AndCanBeRetried()
    {
        // Arrange
        var failures = new ForcedConcurrencyFailures(5);
        await using var host = new EnrollmentTestHost(failures);
        var (scenario, booking) = await WaitlistTestData.FullAsync(host);
        var waiting = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        var entryId = await ApiAssertions.CreatedIdAsync(joined);
        var before = await host.DbAsync(
            db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync());
        failures.UserId = waiting;

        // Act
        using var response = await host.CancelBookingAsync(scenario.UserId, booking);

        // Assert
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

        // Act
        using var recovered = await host.CancelBookingAsync(scenario.UserId, booking);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == waiting)));
    }
}
