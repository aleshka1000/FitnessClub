using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class AnalyticsApiTests
{
    [TestMethod]
    public async Task MultipleRooms_AreAggregatedSeparately_AndOrderedByDateAndRoom()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var start = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(scenario.UserId * 3);
        var secondRoom = await host.DbAsync(async db =>
        {
            var room = new Room { Name = "Second analytics room" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            return room.Id;
        });
        var second = await host.AddScheduledClassAsync(secondRoom, start.AddHours(1), 20);
        var first = await host.AddScheduledClassAsync(scenario.RoomId, start.AddHours(2), 10);
        await host.AddScheduledClassAsync(scenario.RoomId, start.AddDays(1), 30);
        await host.AddScheduledClassAsync(scenario.RoomId, start.AddHours(3), 100, true);
        await host.DbAsync(async db =>
        {
            db.Enrollments.AddRange(
                new Enrollment
                {
                    ClassId = first,
                    UserId = scenario.UserId,
                    Seats = 2,
                    Status = "active",
                    CreatedAt = start
                },
                new Enrollment
                {
                    ClassId = first,
                    UserId = scenario.UserId,
                    Seats = 3,
                    Status = "active",
                    CreatedAt = start
                },
                new Enrollment
                {
                    ClassId = second,
                    UserId = scenario.UserId,
                    Seats = 1,
                    Status = "active",
                    CreatedAt = start
                });

            return await db.SaveChangesAsync();
        });
        using var client = host.Client(scenario.UserId, "admin");

        // Act
        var body = await ApiAssertions.GetJsonAsync(client,
            $"/analytics/load?from={start:yyyy-MM-dd}&to={start.AddDays(1):yyyy-MM-dd}");

        // Assert
        var rows = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.AreEqual(3, rows.Length);
        CollectionAssert.AreEqual(new[] { scenario.RoomId, secondRoom, scenario.RoomId },
            rows.Select(x => x.GetProperty("room").GetProperty("id").GetInt64()).ToArray());
        CollectionAssert.AreEqual(
            new[] { 10, 20, 30 },
            rows.Select(x => x.GetProperty("totalCapacity").GetInt32()).ToArray());
        CollectionAssert.AreEqual(
            new[] { 5, 1, 0 },
            rows.Select(x => x.GetProperty("bookedSeats").GetInt32()).ToArray());
        Assert.IsTrue(rows.All(x => x.GetProperty("classesCount").GetInt32() == 1));
        Assert.AreEqual(DateOnly.FromDateTime(start), DateOnly.Parse(rows[0].GetProperty("date").GetString()!));
        Assert.AreEqual(
            DateOnly.FromDateTime(start.AddDays(1)),
            DateOnly.Parse(rows[2].GetProperty("date").GetString()!));
    }

    [TestMethod]
    public async Task SingleDayRange_IncludesAllClassesStartingThatDay()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        await host.AddScheduledClassAsync(scenario.RoomId, Start);
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddHours(23));
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddDays(1));
        using var client = host.Client(scenario.UserId, "admin");

        // Act
        var body = await ApiAssertions.GetJsonAsync(client,
            $"/analytics/load?from=2040-01-02&to=2040-01-02&roomId={scenario.RoomId}");

        // Assert
        Assert.AreEqual(1, body.GetProperty("items").GetArrayLength());
        Assert.AreEqual(2, body.GetProperty("items")[0].GetProperty("classesCount").GetInt32());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnknownRoomOrEmptyPeriod_ReturnsNoRows(bool unknownRoom)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        await host.AddScheduledClassAsync(scenario.RoomId, Start);
        using var client = host.Client(scenario.UserId, "admin");
        var room = unknownRoom ? long.MaxValue : scenario.RoomId;
        var day = unknownRoom ? "2040-01-02" : "2040-01-10";

        // Act
        var body = await ApiAssertions.GetJsonAsync(client, $"/analytics/load?from={day}&to={day}&roomId={room}");

        // Assert
        Assert.AreEqual(0, body.GetProperty("items").GetArrayLength());
    }

    private static readonly DateTime Start = new(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Load_AggregatesEachClassOnce_AndIncludesBothDates()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var first = await host.AddScheduledClassAsync(scenario.RoomId, Start, 10);
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddHours(23), 20);
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddDays(1), 30);
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddDays(2), 40);
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddTicks(-10), 50);
        var cancelledClass = await host.AddScheduledClassAsync(scenario.RoomId, Start.AddHours(1), 100, true);
        await host.DbAsync(async db =>
        {
            db.Enrollments.AddRange(
                new Enrollment
                {
                    ClassId = first,
                    UserId = scenario.UserId,
                    Seats = 2,
                    Status = "active",
                    CreatedAt = Start
                },
                new Enrollment
                {
                    ClassId = first,
                    UserId = scenario.UserId,
                    Seats = 3,
                    Status = "active",
                    CreatedAt = Start
                },
                new Enrollment
                {
                    ClassId = first,
                    UserId = scenario.UserId,
                    Seats = 4,
                    Status = "cancelled",
                    CreatedAt = Start
                },
                new Enrollment
                {
                    ClassId = cancelledClass,
                    UserId = scenario.UserId,
                    Seats = 7,
                    Status = "active",
                    CreatedAt = Start
                });
            await db.SaveChangesAsync();

            return true;
        });
        using var admin = host.Client(scenario.UserId, "admin");

        // Act
        var body = await ApiAssertions.GetJsonAsync(
            admin,
            $"/analytics/load?from=2040-01-02&to=2040-01-03&roomId={scenario.RoomId}");

        // Assert
        var items = body.GetProperty("items");
        Assert.AreEqual(2, items.GetArrayLength());
        Assert.AreEqual("2040-01-02", items[0].GetProperty("date").GetString());
        Assert.AreEqual(2, items[0].GetProperty("classesCount").GetInt32());
        Assert.AreEqual(30, items[0].GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(5, items[0].GetProperty("bookedSeats").GetInt32());
        Assert.AreEqual("2040-01-03", items[1].GetProperty("date").GetString());
        Assert.AreEqual(1, items[1].GetProperty("classesCount").GetInt32());
        Assert.AreEqual(30, items[1].GetProperty("totalCapacity").GetInt32());
        Assert.AreEqual(0, items[1].GetProperty("bookedSeats").GetInt32());
        CollectionAssert.AreEqual(new[] { "items" }, body.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "date", "room", "classesCount", "totalCapacity", "bookedSeats" },
            items[0].EnumerateObject().Select(x => x.Name).ToArray());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("from=2040-01-02")]
    [DataRow("to=2040-01-03")]
    [DataRow("from=&to=2040-01-03")]
    [DataRow("from=2040-01-02&to=")]
    [DataRow("from=%20&to=2040-01-03")]
    [DataRow("from=2040-02-30&to=2040-03-01")]
    [DataRow("from=2040-1-2&to=2040-01-03")]
    [DataRow("from=bad&to=2040-01-03")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03")]
    [DataRow("from=2040-01-02&to=2040-01-03T00:00:00Z")]
    [DataRow("from=2040-01-03&to=2040-01-02")]
    [DataRow("from=2040-01-02&to=2040-01-03&roomId=bad")]
    public async Task InvalidLoad_Returns400(string query)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user, "admin");

        // Act
        using var response = await client.GetAsync($"/analytics/load?{query}");

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(400, (await ApiAssertions.JsonAsync(response)).GetProperty("status").GetInt32());
    }

    [TestMethod]
    public async Task Load_RequiresAdmin()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var regular = host.Client(user);

        // Act
        using var forbidden = await regular.GetAsync("/analytics/load?from=2040-01-02&to=2040-01-03");

        // Assert
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
}
