using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class EnrollmentDetailsApiTests
{
    [TestMethod]
    public async Task Card_ReturnsExactContractAndValues()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await ApiAssertions.CreatedIdAsync(created);

        // Act
        var card = await host.GetCardAsync(scenario.UserId, id);

        // Assert
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

        Assert.AreEqual(id, card.GetProperty("id").GetInt64());
        Assert.AreEqual("active", card.GetProperty("status").GetString());
        Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
        var scheduled = card.GetProperty("class");
        Assert.AreEqual(scenario.ClassId, scheduled.GetProperty("id").GetInt64());
        Assert.AreEqual("Test class", scheduled.GetProperty("title").GetString());
        Assert.AreEqual(host.Clock.Now.UtcDateTime.AddDays(1), scheduled.GetProperty("startsAt").GetDateTime());
        Assert.AreEqual(60, scheduled.GetProperty("durationMinutes").GetInt32());
        Assert.IsFalse(scheduled.GetProperty("isCancelled").GetBoolean());
        Assert.AreEqual(scenario.RoomId, scheduled.GetProperty("room").GetProperty("id").GetInt64());
        Assert.AreEqual(await host.DbAsync(
                db => db.Rooms.Where(x => x.Id == scenario.RoomId).Select(x => x.Name).SingleAsync()),
            scheduled.GetProperty("room").GetProperty("name").GetString());
        Assert.AreEqual(
            (await ApiAssertions.JsonAsync(created)).GetProperty("createdAt").GetDateTime(),
            card.GetProperty("createdAt").GetDateTime());
    }

    [TestMethod]
    public async Task CreatingAnotherBooking_RefreshesPassInExistingCard()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var first = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await ApiAssertions.CreatedIdAsync(first);
        var before = await host.GetCardAsync(scenario.UserId, id);
        Assert.AreEqual(8, before.GetProperty("pass").GetProperty("remainingVisits").GetInt32());

        // Act
        using var second = await host.PostAsync(scenario.UserId, scenario.ClassId);

        // Assert
        await ApiAssertions.CreatedIdAsync(second);
        var after = await host.GetCardAsync(scenario.UserId, id);
        Assert.AreEqual(7, after.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
        Assert.AreEqual(before.GetProperty("seats").GetInt32(), after.GetProperty("seats").GetInt32());
    }

    [TestMethod]
    public async Task CancelledBookingOnCancelledClass_RemainsVisibleWithOriginalSeats()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId, 2);
        var id = await ApiAssertions.CreatedIdAsync(created);
        await host.DbAsync(db => db.Classes.Where(x => x.Id == scenario.ClassId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsCancelled, true)));
        using var cancelled = await host.CancelBookingAsync(scenario.UserId, id);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);

        // Act
        var card = await host.GetCardAsync(scenario.UserId, id);

        // Assert
        Assert.AreEqual("cancelled", card.GetProperty("status").GetString());
        Assert.AreEqual(2, card.GetProperty("seats").GetInt32());
        Assert.IsTrue(card.GetProperty("class").GetProperty("isCancelled").GetBoolean());
        Assert.AreEqual(10, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
    }
}
