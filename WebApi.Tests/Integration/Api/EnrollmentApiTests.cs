using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentApiTests
{
    [TestMethod]
    public async Task Create_ReturnsExactContract_AndChargesSeatsOnce()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        host.Clock.Now = host.Clock.Now.AddTicks(7);
        var scenario = await host.ScenarioAsync();

        // Act
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiAssertions.JsonAsync(response);
        CollectionAssert.AreEquivalent(new[] { "id", "classId", "seats", "status", "createdAt" },
            body.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(scenario.ClassId, body.GetProperty("classId").GetInt64());
        Assert.AreEqual(2, body.GetProperty("seats").GetInt32());
        Assert.AreEqual("active", body.GetProperty("status").GetString());
        var id = body.GetProperty("id").GetInt64();
        var createdAt = body.GetProperty("createdAt").GetDateTime();
        Assert.AreEqual(DateTimeKind.Utc, createdAt.Kind);
        Assert.AreEqual(0L, createdAt.Ticks % TimeSpan.TicksPerMicrosecond);
        await host.DbAsync(async db =>
        {
            Assert.AreEqual(createdAt, (await db.Enrollments.SingleAsync(x => x.Id == id)).CreatedAt);
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);
            Assert.AreEqual(8, pass.RemainingVisits);
            Assert.AreNotEqual(scenario.PassVersion, pass.Version);
            var scheduledClass = await db.Classes.SingleAsync(x => x.Id == scenario.ClassId);
            Assert.AreNotEqual(scenario.ClassVersion, scheduledClass.Version);
            Assert.AreEqual(pass.Version, scheduledClass.Version);
            Assert.AreEqual(
                id,
                (await db.IdempotencyRequests.SingleAsync(x => x.UserId == scenario.UserId)).EnrollmentId);

            return true;
        });
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"classId\":1}")]
    [DataRow("{\"seats\":1}")]
    [DataRow("{\"classId\":null,\"seats\":1}")]
    [DataRow("{\"classId\":1,\"seats\":null}")]
    [DataRow("{\"classId\":1,\"seats\":0}")]
    [DataRow("{\"classId\":1,\"seats\":-1}")]
    [DataRow("{\"classId\":\"1\",\"seats\":1}")]
    [DataRow("{\"classId\":1,\"seats\":\"1\"}")]
    [DataRow("{\"classId\":\"bad\",\"seats\":1}")]
    [DataRow("{\"classId\":1.5,\"seats\":1}")]
    [DataRow("{\"classId\":9223372036854775808,\"seats\":1}")]
    [DataRow("{\"classId\":1,\"seats\":2147483648}")]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("")]
    [DataRow(null)]
    public async Task InvalidBody_Returns400_WithoutChanges(string? json)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();

        // Act
        using var response = await host.PostJsonAsync(scenario.UserId, json, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(400, (await ApiAssertions.JsonAsync(response)).GetProperty("status").GetInt32());
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task InvalidKey_Returns400_WithoutChanges(string? key)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();

        // Act
        using var response = await host.PostJsonAsync(scenario.UserId,
            JsonSerializer.Serialize(new { classId = scenario.ClassId, seats = 1 }), key);

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    public async Task UnknownFields_AreIgnored_AndClientCannotChooseUser()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var otherUser = await host.AddUserAsync();

        // Act
        using var response = await host.PostJsonAsync(scenario.UserId,
            JsonSerializer.Serialize(
                new { classId = scenario.ClassId, seats = 1, userId = otherUser, other = true }), "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var id = (await ApiAssertions.JsonAsync(response)).GetProperty("id").GetInt64();
        Assert.AreEqual(scenario.UserId, await host.DbAsync(db => db.Enrollments
                    .Where(x => x.Id == id).Select(x => x.UserId).SingleAsync()));
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    public async Task MissingClass_Returns404_WithoutChanges(long classId)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();

        // Act
        using var response = await host.PostAsync(scenario.UserId, classId);

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    [DataRow("class-cancelled")]
    [DataRow("class-started")]
    [DataRow("pass-expired")]
    [DataRow("capacity-exceeded")]
    [DataRow("pass-exhausted")]
    public async Task BusinessRefusal_ReturnsReason_AndLeavesNoTrace(string reason)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(
            capacity: reason == "capacity-exceeded" ? 1 : 4,
            visits: reason == "pass-exhausted" ? 0 : 10,
            startsIn: reason == "class-started" ? TimeSpan.Zero : null,
            cancelled: reason == "class-cancelled",
            validUntil: reason == "pass-expired"
                ? DateOnly.FromDateTime(host.Clock.Now.UtcDateTime).AddDays(-1)
                : null);

        // Act
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);

        // Assert
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ApiAssertions.JsonAsync(response);
        Assert.AreEqual(409, body.GetProperty("status").GetInt32());
        Assert.AreEqual(reason, body.GetProperty("reason").GetString());
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        await host.AssertUnchangedAsync(scenario);
    }

    [TestMethod]
    public async Task ValidUntil_IsInclusive_AndIndependentOfClassDate()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        host.Clock.Now = new DateTimeOffset(2030, 1, 2, 23, 59, 59, TimeSpan.Zero).AddTicks(9999999);
        var scenario = await host.ScenarioAsync(startsIn: TimeSpan.FromDays(30), validUntil: new DateOnly(2030, 1, 2));

        // Act
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId);

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelledClass_TakesPrecedenceOverStartedClass()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(startsIn: TimeSpan.FromDays(-1), cancelled: true, visits: 0);

        // Act
        using var response = await host.PostAsync(scenario.UserId, scenario.ClassId);

        // Assert
        Assert.AreEqual("class-cancelled", (await ApiAssertions.JsonAsync(response)).GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task MultipleBookings_AreAllowed_AndCancelledSeatsDoNotCount()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(capacity: 2);
        await host.DbAsync(async db =>
        {
            db.Enrollments.Add(new Enrollment
                {
                    UserId = scenario.UserId,
                    ClassId = scenario.ClassId,
                    Seats = 100,
                    Status = "cancelled",
                    CreatedAt = host.Clock.Now.UtcDateTime,
                    CancelledAt = host.Clock.Now.UtcDateTime
                });

            return await db.SaveChangesAsync();
        });

        // Act
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId);
        using var second = await host.PostAsync(scenario.UserId, scenario.ClassId);
        using var third = await host.PostAsync(scenario.UserId, scenario.ClassId);

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, third.StatusCode);
        Assert.AreEqual(8, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                    .Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    public async Task RepeatedKey_ReturnsSameBooking_WithoutChargingAgain()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "same-key");

        // Act
        using var second = await host.PostJsonAsync(scenario.UserId,
            $"{{\"ignored\":true,\"seats\":2,\"classId\":{scenario.ClassId}}}", "same-key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.AreEqual(1, await host.DbAsync(db => db.Enrollments.CountAsync(x => x.UserId == scenario.UserId)));
        Assert.AreEqual(8, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                    .Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    [DataRow("active", 3)]
    [DataRow("cancelled", 2)]
    public async Task RepeatedKey_ReturnsCurrentState_EvenWhenNewBookingWouldFail(string status, int seats)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");
        var id = (await ApiAssertions.JsonAsync(first)).GetProperty("id").GetInt64();
        await host.DbAsync(async db =>
        {
            var enrollment = await db.Enrollments.SingleAsync(x => x.Id == id);
            enrollment.Status = status;
            enrollment.Seats = seats;
            var scheduledClass = await db.Classes.SingleAsync(x => x.Id == scenario.ClassId);
            scheduledClass.IsCancelled = true;
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);
            pass.RemainingVisits = 0;

            return await db.SaveChangesAsync();
        });

        // Act
        using var repeated = await host.PostAsync(scenario.UserId, scenario.ClassId, 2, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, repeated.StatusCode);
        var body = await ApiAssertions.JsonAsync(repeated);
        Assert.AreEqual(id, body.GetProperty("id").GetInt64());
        Assert.AreEqual(status, body.GetProperty("status").GetString());
        Assert.AreEqual(seats, body.GetProperty("seats").GetInt32());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReusedKeyWithDifferentRequest_Returns409(bool changeSeats)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key");

        // Act
        using var second = await host.PostAsync(scenario.UserId,
            changeSeats ? scenario.ClassId : long.MaxValue, changeSeats ? 2 : 1, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.AreEqual("idempotency-key-reused", (await ApiAssertions.JsonAsync(second))
                .GetProperty("reason").GetString());
        Assert.AreEqual(9, await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                    .Select(x => x.RemainingVisits).SingleAsync()));
    }

    [TestMethod]
    public async Task FailedRequest_DoesNotReserveKey()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync(visits: 0);

        // Act
        using var failed = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Conflict, failed.StatusCode);

        // Arrange
        await host.DbAsync(db => db.Passes.Where(x => x.UserId == scenario.UserId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RemainingVisits, 1)));

        // Act
        using var succeeded = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, succeeded.StatusCode);
    }

    [TestMethod]
    public async Task Key_IsScopedToUser_AndCaseSensitive()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var other = await host.AddUserAsync();

        // Act
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, "Key");
        using var second = await host.PostAsync(other, scenario.ClassId, 1, "Key");
        using var third = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, "key");

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, third.StatusCode);
        var ids = await Task.WhenAll(new[] { first, second, third }.Select(ApiAssertions.JsonAsync));
        Assert.AreEqual(3, ids.Select(x => x.GetProperty("id").GetInt64()).Distinct().Count());
    }

    [TestMethod]
    public async Task LongKey_IsPreserved_AndHasFixedSizeIndexValue()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(6000));

        // Act
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, key);
        using var second = await host.PostAsync(scenario.UserId, scenario.ClassId, 1, key);

        // Assert
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await host.DbAsync(async db =>
        {
            var record = await db.IdempotencyRequests.SingleAsync(x => x.UserId == scenario.UserId);
            Assert.AreEqual(key, record.OriginalKey);
            CollectionAssert.AreEqual(SHA256.HashData(Encoding.UTF8.GetBytes(key)), record.KeyHash);

            return true;
        });
    }
}
