using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class ScheduleApiTests
{
    [TestMethod]
    public async Task PaginationAcrossTimesAndRooms_IsOrderedAndLimitedInDatabase()
    {
        // Arrange
        var queries = new SqlCommandRecorder();
        await using var host = new EnrollmentTestHost(queries);
        var scenario = await host.ScenarioAsync();
        var start = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(scenario.UserId * 3);
        var secondRoom = await host.DbAsync(async db =>
        {
            var room = new Room { Name = "Second schedule room" };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();

            return room.Id;
        });
        var late = await host.AddScheduledClassAsync(scenario.RoomId, start.AddMinutes(2), 40);
        var first = await host.AddScheduledClassAsync(secondRoom, start, 20);
        var tied = await host.AddScheduledClassAsync(scenario.RoomId, start, 30);
        var middle = await host.AddScheduledClassAsync(secondRoom, start.AddMinutes(1), 50);
        var roomNames = await host.DbAsync(db => db.Rooms.Where(x => x.Id == scenario.RoomId || x.Id == secondRoom)
                .ToDictionaryAsync(x => x.Id, x => x.Name));
        var capacities = new Dictionary<long, int> { [late] = 40, [first] = 20, [tied] = 30, [middle] = 50 };
        var actual = new List<long>();
        var pages = 0;
        string? token = null;
        using var client = host.Client(scenario.UserId);

        do
        {
            // Arrange
            var path = $"/schedule?from={start:O}&to={start.AddMinutes(3):O}&limit=2";

            if (token is not null)
                path += $"&continuationToken={Uri.EscapeDataString(token)}";

            queries.Commands.Clear();

            // Act
            var body = await ApiAssertions.GetJsonAsync(client, path);

            // Assert
            Assert.AreEqual(1, queries.Commands.Count);
            StringAssert.Contains(queries.Commands.Single().ToUpperInvariant(), "LIMIT");
            Assert.IsTrue(body.GetProperty("items").GetArrayLength() <= 2);

            foreach (var item in body.GetProperty("items").EnumerateArray())
            {
                var id = item.GetProperty("classId").GetInt64();
                actual.Add(id);
                Assert.AreEqual("Query test", item.GetProperty("title").GetString());
                Assert.AreEqual(60, item.GetProperty("durationMinutes").GetInt32());
                Assert.AreEqual(capacities[id], item.GetProperty("capacity").GetInt32());
                Assert.AreEqual(capacities[id], item.GetProperty("freeSeats").GetInt32());
                Assert.AreEqual(0, item.GetProperty("mySeats").GetInt32());
                var room = item.GetProperty("room");
                Assert.AreEqual(roomNames[room.GetProperty("id").GetInt64()], room.GetProperty("name").GetString());
            }

            token = body.GetProperty("continuationToken").GetString();
            Assert.IsTrue(++pages <= 2);
        } while (token is not null);

        // Assert
        CollectionAssert.AreEqual(new[] { first, tied, middle, late }, actual);
    }

    [TestMethod]
    public async Task UnknownRoom_ReturnsEmptyPage()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        await host.AddScheduledClassAsync(scenario.RoomId, Start);
        using var client = host.Client(scenario.UserId);

        // Act
        var body = await ApiAssertions.GetJsonAsync(client, ScheduleUrl(long.MaxValue));

        // Assert
        Assert.AreEqual(0, body.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("continuationToken").ValueKind);
    }

    private static readonly DateTime Start = new(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static string ScheduleUrl(long roomId, string extra = "") =>
        $"/schedule?from=2040-01-02T00:00:00&to=2040-01-03T00:00:00Z&roomId={roomId}{extra}";

    [TestMethod]
    public async Task Schedule_AggregatesActiveBookings_AndRespectsPeriodAndCancellation()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var id = await host.AddScheduledClassAsync(scenario.RoomId, Start);
        var empty = await host.AddScheduledClassAsync(scenario.RoomId, Start.AddHours(23));
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddTicks(-10));
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddDays(1));
        await host.AddScheduledClassAsync(scenario.RoomId, Start.AddHours(1), cancelled: true);
        var other = await host.AddUserAsync();
        using var first = await host.PostAsync(scenario.UserId, id, 2);
        using var second = await host.PostAsync(scenario.UserId, id, 3);
        using var third = await host.PostAsync(other, id, 1);
        using var toCancel = await host.PostAsync(other, id, 2);
        var cancelledId = await ApiAssertions.CreatedIdAsync(toCancel);
        using var cancel = await host.CancelBookingAsync(other, cancelledId);
        using var client = host.Client(scenario.UserId);

        // Act
        var body = await ApiAssertions.GetJsonAsync(client, ScheduleUrl(scenario.RoomId));

        // Assert
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[] { id, empty },
            items.Select(x => x.GetProperty("classId").GetInt64()).ToArray());
        Assert.AreEqual(4, items[0].GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(5, items[0].GetProperty("mySeats").GetInt32());
        Assert.AreEqual(10, items[1].GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(0, items[1].GetProperty("mySeats").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("continuationToken").ValueKind);
        CollectionAssert.AreEquivalent(
            new[] { "items", "continuationToken" },
            body.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[]
            {
                "classId",
                "title",
                "startsAt",
                "durationMinutes",
                "room",
                "capacity",
                "freeSeats",
                "mySeats"
            },
            items[0].EnumerateObject().Select(x => x.Name).ToArray());

        // Arrange
        await host.DbAsync(
            db => db.Classes.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(
                    x => x.Capacity,
                    1)));

        // Act
        body = await ApiAssertions.GetJsonAsync(client, ScheduleUrl(scenario.RoomId));

        // Assert
        Assert.AreEqual(0, body.GetProperty("items")[0].GetProperty("freeSeats").GetInt32());
    }

    [TestMethod]
    public async Task Schedule_PaginatesEqualTimestamps_AndAllowsChangingPageSize()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var expected = new List<long>();

        for (var i = 0; i < 23; i++)
            expected.Add(await host.AddScheduledClassAsync(scenario.RoomId, Start));

        using var client = host.Client(scenario.UserId);

        // Act
        var body = await ApiAssertions.GetJsonAsync(client, ScheduleUrl(scenario.RoomId));

        // Assert
        Assert.AreEqual(20, body.GetProperty("items").GetArrayLength());
        var actual = body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64())
            .ToList();
        var token = body.GetProperty("continuationToken").GetString();
        Assert.IsNotNull(token);

        // Act
        using var wrongFilters = await client.GetAsync(ScheduleUrl(scenario.RoomId + 1,
                $"&continuationToken={Uri.EscapeDataString(token)}"));

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongFilters.StatusCode);

        // Act
        using var wrongPeriod = await client.GetAsync(ScheduleUrl(scenario.RoomId,
                $"&continuationToken={Uri.EscapeDataString(token)}").Replace("2040-01-03", "2040-01-04"));

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongPeriod.StatusCode);

        // Act
        while (token is not null)
        {
            body = await ApiAssertions.GetJsonAsync(client, ScheduleUrl(scenario.RoomId,
                    $"&limit=2&continuationToken={Uri.EscapeDataString(token)}"));
            actual.AddRange(
                body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()));
            token = body.GetProperty("continuationToken").GetString();
        }

        // Assert
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("2040-01-02T00:00:00", "2040-01-03T00:00:00")]
    [DataRow("2040-01-02T00:00:00Z", "2040-01-03T00:00:00Z")]
    [DataRow("2040-01-02T05:00:00+05:00", "2040-01-03T05:00:00+05:00")]
    [DataRow("2040-01-01T19:00:00-05:00", "2040-01-02T19:00:00-05:00")]
    public async Task Schedule_NormalizesOffsets(string from, string to)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var id = await host.AddScheduledClassAsync(scenario.RoomId, Start);
        using var client = host.Client(scenario.UserId);

        // Act
        var body = await ApiAssertions.GetJsonAsync(client,
            $"/schedule?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&roomId={scenario.RoomId}");

        // Assert
        Assert.AreEqual(id, body.GetProperty("items")[0].GetProperty("classId").GetInt64());
        Assert.AreEqual("2040-01-02T00:00:00Z", body.GetProperty("items")[0].GetProperty("startsAt").GetString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("from=2040-01-02T00:00:00Z")]
    [DataRow("to=2040-01-03T00:00:00Z")]
    [DataRow("from=&to=2040-01-03T00:00:00Z")]
    [DataRow("from=2040-01-02T00:00:00Z&to=")]
    [DataRow("from=%20&to=2040-01-03T00:00:00Z")]
    [DataRow("from=2040-02-30T00:00:00Z&to=2040-03-01T00:00:00Z")]
    [DataRow("from=bad&to=2040-01-03T00:00:00Z")]
    [DataRow("from=2040-01-02&to=2040-01-03")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-02T00:00:00Z")]
    [DataRow("from=2040-01-03T00:00:00Z&to=2040-01-02T00:00:00Z")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&limit=0")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&limit=101")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&limit=bad")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&roomId=bad")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&continuationToken=bad")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&continuationToken=")]
    [DataRow("from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&continuationToken=e30")]
    public async Task InvalidSchedule_Returns400(string query)
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user);

        // Act
        using var response = await client.GetAsync($"/schedule?{query}");

        // Assert
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(400, (await ApiAssertions.JsonAsync(response)).GetProperty("status").GetInt32());
    }
}
