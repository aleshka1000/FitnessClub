using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentSeatChangeApiTests
{
    [TestMethod]
    public async Task Increase_RequiresVisitsOnlyForTheDifference()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 2, visits: 2);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await ApiAssertions.CreatedIdAsync(created);

        // Act
        using var changed = await host.ChangeSeatsAsync(scenario.UserId, id, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        var card = await ApiAssertions.JsonAsync(changed);
        Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(0, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }

    [TestMethod]
    [DataRow("enrollment-cancelled")]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    public async Task Decrease_StillValidatesBookingAndClassState(string reason)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await ApiAssertions.CreatedIdAsync(created);
        await host.DbAsync(async db =>
        {
            if (reason == "enrollment-cancelled")
                (await db.Enrollments.SingleAsync(x => x.Id == id)).Status = "cancelled";
            else
            {
                var scheduledClass = await db.Classes.SingleAsync(x => x.Id == scenario.ClassId);

                if (reason == "class-cancelled")
                    scheduledClass.IsCancelled = true;
                else
                    scheduledClass.StartsAt = host.Clock.Now.UtcDateTime;
            }

            return await db.SaveChangesAsync();
        });
        var before = await host.GetClassStateAsync(scenario.ClassId, scenario.UserId);

        // Act
        using var changed = await host.ChangeSeatsAsync(scenario.UserId, id, 1);

        // Assert
        await ApiAssertions.ConflictAsync(changed, reason);
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId, scenario.UserId));
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
        // Arrange
        await using var host = new EnrollmentTestHost();

        // Act
        using var response = await host.SendAsync(1, "/enrollments/1/seats", json);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task IncreaseAndDecrease_UseDifference_AndReturnFullCard()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(startsIn: TimeSpan.FromMinutes(30));
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await ApiAssertions.CreatedIdAsync(created);

        // Act
        using var increased = await host.SendAsync(scenario.UserId, $"/enrollments/{id}/seats",
            "{\"seats\":4,\"ignored\":true}");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, increased.StatusCode);
        var card = await ApiAssertions.JsonAsync(increased);
        Assert.AreEqual(4, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(scenario.RoomId, card.GetProperty("class").GetProperty("room").GetProperty("id").GetInt64());

        // Arrange
        await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.ValidUntil, new DateOnly(2029, 1, 1)).SetProperty(x => x.RemainingVisits, 0)));

        // Act
        using var decreased = await host.ChangeSeatsAsync(scenario.UserId, id, 1);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, decreased.StatusCode);
        card = await ApiAssertions.JsonAsync(decreased);
        Assert.AreEqual(1, card.GetProperty("seats").GetInt32());
        Assert.AreEqual(3, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        // Arrange
        var before = await host.DbAsync(
            db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync());

        // Act
        using var unchanged = await host.ChangeSeatsAsync(scenario.UserId, id, 1);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.AreEqual(
            before,
            await host.DbAsync(
                db => db.Classes.Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync()));
        Assert.AreEqual((await host.GetCardAsync(scenario.UserId, id)).GetRawText(),
            (await ApiAssertions.JsonAsync(unchanged)).GetRawText());
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
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 1);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await ApiAssertions.CreatedIdAsync(created);
        await host.DbAsync(async db =>
        {
            var booking = await db.Enrollments.SingleAsync(x => x.Id == id);
            var scheduledClass = await db.Classes.SingleAsync(x => x.Id == scenario.ClassId);
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);

            if (reason == "enrollment-cancelled")
                booking.Status = "cancelled";

            if (reason == "class-cancelled")
                scheduledClass.IsCancelled = true;

            if (reason == "class-started")
                scheduledClass.StartsAt = host.Clock.Now.UtcDateTime;

            if (reason == "pass-expired")
                pass.ValidUntil = new DateOnly(2029, 1, 1);

            if (reason == "pass-exhausted")
            {
                pass.RemainingVisits = 0;
                scheduledClass.Capacity = 4;
            }

            await db.SaveChangesAsync();

            return true;
        });
        var before = await host.GetClassStateAsync(scenario.ClassId, scenario.UserId);

        // Act
        using var response = await host.ChangeSeatsAsync(scenario.UserId, id, seats);

        // Assert
        await ApiAssertions.ConflictAsync(response, reason);
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId, scenario.UserId));
    }

    [TestMethod]
    public async Task Decrease_PromotesOwnWaitlist_AndReturnsFinalPassBalance()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 4);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 4);
        var id = await ApiAssertions.CreatedIdAsync(created);
        using var joined = await host.JoinAsync(scenario.UserId, scenario.ClassId, 2);
        var waitingId = await ApiAssertions.CreatedIdAsync(joined);

        // Act
        using var changed = await host.ChangeSeatsAsync(scenario.UserId, id, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
        var card = await ApiAssertions.JsonAsync(changed);
        Assert.AreEqual(6, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(
            "promoted",
            await host.DbAsync(
                db => db.WaitlistEntries.Where(x => x.Id == waitingId).Select(x => x.Status).SingleAsync()));
        Assert.AreEqual(
            4,
            await host.DbAsync(
                db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
    }

    [TestMethod]
    public async Task FailedPromotion_RollsBackSeatChangeAndRefund()
    {
        // Arrange
        var failures = new ForcedConcurrencyFailures(5);
        await using var host = new EnrollmentTestHost(failures);
        var scenario = await host.ScenarioAsync(capacity: 4);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 4);
        var id = await ApiAssertions.CreatedIdAsync(created);
        var waiting = await host.AddUserAsync();
        using var joined = await host.JoinAsync(waiting, scenario.ClassId, 2);
        await ApiAssertions.CreatedIdAsync(joined);
        var before = await host.GetClassStateAsync(scenario.ClassId, waiting);
        failures.UserId = waiting;

        // Act
        using var response = await host.ChangeSeatsAsync(scenario.UserId, id, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(before, await host.GetClassStateAsync(scenario.ClassId, waiting));

        // Act
        using var retry = await host.ChangeSeatsAsync(scenario.UserId, id, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode);
    }

    [TestMethod]
    public async Task IncreaseRacesWithBooking_OnlyOneTakesLastSeat()
    {
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync(capacity: 2);
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await ApiAssertions.CreatedIdAsync(created);
        var other = await host.AddUserAsync();
        barrier.ClassId = scenario.ClassId;

        // Act
        var responses = await Task.WhenAll(
            host.ChangeSeatsAsync(
                scenario.UserId,
                id,
                2),
            host.PostAsync(
                other,
                scenario.ClassId));

        // Assert
        try
        {
            Assert.AreEqual(1, responses.Count(x => x.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created));
            await ApiAssertions.ConflictAsync(
                responses.Single(x => x.StatusCode == HttpStatusCode.Conflict),
                "capacity-exceeded");
            Assert.AreEqual(
                2,
                await host.DbAsync(
                    db => db.Enrollments.Where(x => x.ClassId == scenario.ClassId && x.Status == "active").SumAsync(x => x.Seats)));
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [TestMethod]
    public async Task IncreaseRacesWithCancellation_VisitsAreAccountedOnce()
    {
        // Arrange
        var barrier = new ArmedSaveBarrier();
        await using var host = new EnrollmentTestHost(barrier);
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await ApiAssertions.CreatedIdAsync(created);
        barrier.ClassId = scenario.ClassId;

        // Act
        var responses = await Task.WhenAll(
            host.ChangeSeatsAsync(
                scenario.UserId,
                id,
                2),
            host.CancelBookingAsync(
                scenario.UserId,
                id));

        // Assert
        try
        {
            Assert.AreEqual(HttpStatusCode.OK, responses[1].StatusCode);
            Assert.IsTrue(responses[0].StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);

            if (responses[0].StatusCode == HttpStatusCode.Conflict)
                await ApiAssertions.ConflictAsync(responses[0], "enrollment-cancelled");

            var card = await host.GetCardAsync(scenario.UserId, id);
            Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
            Assert.AreEqual(10, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }
}
