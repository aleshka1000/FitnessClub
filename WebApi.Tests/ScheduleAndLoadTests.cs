using System.Net;
using System.Text.Json;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class ScheduleAndLoadTests
{
    private static readonly DateTime Start = new(2040, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static string ScheduleUrl(long roomId, string extra = "") =>
        $"/schedule?from=2040-01-02T00:00:00&to=2040-01-03T00:00:00Z&roomId={roomId}{extra}";

    private static Task<long> AddClassAsync(EnrollmentTestHost host, long roomId, DateTime start,
        int capacity = 10, bool cancelled = false) => host.DbAsync(async db =>
    {
        var scheduledClass = new Class
        {
            RoomId = roomId, Title = "Query test", StartsAt = start, Capacity = capacity,
            DurationMinutes = 60, IsCancelled = cancelled
        };
        db.Classes.Add(scheduledClass);
        await db.SaveChangesAsync();
        return scheduledClass.Id;
    });

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await EnrollmentTestHost.JsonAsync(response);
    }

    [TestMethod]
    public async Task Schedule_AggregatesActiveBookings_AndRespectsPeriodAndCancellation()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var id = await AddClassAsync(host, scenario.RoomId, Start);
        var empty = await AddClassAsync(host, scenario.RoomId, Start.AddHours(23));
        await AddClassAsync(host, scenario.RoomId, Start.AddTicks(-10));
        await AddClassAsync(host, scenario.RoomId, Start.AddDays(1));
        await AddClassAsync(host, scenario.RoomId, Start.AddHours(1), cancelled: true);
        var other = await host.AddUserAsync();
        using var first = await host.PostAsync(scenario.UserId, id, 2);
        using var second = await host.PostAsync(scenario.UserId, id, 3);
        using var third = await host.PostAsync(other, id, 1);
        using var toCancel = await host.PostAsync(other, id, 2);
        var cancelledId = await WaitlistTestData.IdAsync(toCancel);
        using var cancel = await host.CancelBookingAsync(other, cancelledId);
        using var client = host.Client(scenario.UserId);
        var body = await GetAsync(client, ScheduleUrl(scenario.RoomId));
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { id, empty }, items.Select(x => x.GetProperty("classId").GetInt64()).ToArray());
        Assert.AreEqual(4, items[0].GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(5, items[0].GetProperty("mySeats").GetInt32());
        Assert.AreEqual(10, items[1].GetProperty("freeSeats").GetInt32());
        Assert.AreEqual(0, items[1].GetProperty("mySeats").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("continuationToken").ValueKind);
        CollectionAssert.AreEquivalent(new[] { "items", "continuationToken" }, body.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "classId", "title", "startsAt", "durationMinutes", "room", "capacity", "freeSeats", "mySeats" },
            items[0].EnumerateObject().Select(x => x.Name).ToArray());
        await host.DbAsync(db => db.Classes.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Capacity, 1)));
        body = await GetAsync(client, ScheduleUrl(scenario.RoomId));
        Assert.AreEqual(0, body.GetProperty("items")[0].GetProperty("freeSeats").GetInt32());
    }

    [TestMethod]
    public async Task Schedule_PaginatesEqualTimestamps_AndAllowsChangingPageSize()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var expected = new List<long>();
        for (var i = 0; i < 23; i++) expected.Add(await AddClassAsync(host, scenario.RoomId, Start));
        using var client = host.Client(scenario.UserId);
        var body = await GetAsync(client, ScheduleUrl(scenario.RoomId));
        Assert.AreEqual(20, body.GetProperty("items").GetArrayLength());
        var actual = body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()).ToList();
        var token = body.GetProperty("continuationToken").GetString();
        Assert.IsNotNull(token);
        using var wrongFilters = await client.GetAsync(ScheduleUrl(scenario.RoomId + 1,
            $"&continuationToken={Uri.EscapeDataString(token)}"));
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongFilters.StatusCode);
        using var wrongPeriod = await client.GetAsync(ScheduleUrl(scenario.RoomId,
            $"&continuationToken={Uri.EscapeDataString(token)}").Replace("2040-01-03", "2040-01-04"));
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongPeriod.StatusCode);
        while (token is not null)
        {
            body = await GetAsync(client, ScheduleUrl(scenario.RoomId,
                $"&limit=2&continuationToken={Uri.EscapeDataString(token)}"));
            actual.AddRange(body.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("classId").GetInt64()));
            token = body.GetProperty("continuationToken").GetString();
        }
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("2040-01-02T00:00:00", "2040-01-03T00:00:00")]
    [DataRow("2040-01-02T00:00:00Z", "2040-01-03T00:00:00Z")]
    [DataRow("2040-01-02T05:00:00+05:00", "2040-01-03T05:00:00+05:00")]
    [DataRow("2040-01-01T19:00:00-05:00", "2040-01-02T19:00:00-05:00")]
    public async Task Schedule_NormalizesOffsets_AndUnknownRoomReturnsEmpty(string from, string to)
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var id = await AddClassAsync(host, scenario.RoomId, Start);
        using var client = host.Client(scenario.UserId);
        var body = await GetAsync(client,
            $"/schedule?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&roomId={scenario.RoomId}");
        Assert.AreEqual(id, body.GetProperty("items")[0].GetProperty("classId").GetInt64());
        Assert.AreEqual("2040-01-02T00:00:00Z", body.GetProperty("items")[0].GetProperty("startsAt").GetString());
        body = await GetAsync(client, ScheduleUrl(long.MaxValue));
        Assert.AreEqual(0, body.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, body.GetProperty("continuationToken").ValueKind);
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
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user);
        using var response = await client.GetAsync($"/schedule?{query}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(400, (await EnrollmentTestHost.JsonAsync(response)).GetProperty("status").GetInt32());
    }

    [TestMethod]
    public async Task Load_AggregatesEachClassOnce_AndIncludesBothDates()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        var first = await AddClassAsync(host, scenario.RoomId, Start, 10);
        await AddClassAsync(host, scenario.RoomId, Start.AddHours(23), 20);
        await AddClassAsync(host, scenario.RoomId, Start.AddDays(1), 30);
        await AddClassAsync(host, scenario.RoomId, Start.AddDays(2), 40);
        await AddClassAsync(host, scenario.RoomId, Start.AddTicks(-10), 50);
        var cancelledClass = await AddClassAsync(host, scenario.RoomId, Start.AddHours(1), 100, true);
        await host.DbAsync(async db =>
        {
            db.Enrollments.AddRange(
                new Enrollment { ClassId = first, UserId = scenario.UserId, Seats = 2, Status = "active", CreatedAt = Start },
                new Enrollment { ClassId = first, UserId = scenario.UserId, Seats = 3, Status = "active", CreatedAt = Start },
                new Enrollment { ClassId = first, UserId = scenario.UserId, Seats = 4, Status = "cancelled", CreatedAt = Start },
                new Enrollment { ClassId = cancelledClass, UserId = scenario.UserId, Seats = 7, Status = "active", CreatedAt = Start });
            await db.SaveChangesAsync();
            return true;
        });
        using var admin = host.Client(scenario.UserId, "admin");
        var body = await GetAsync(admin, $"/analytics/load?from=2040-01-02&to=2040-01-03&roomId={scenario.RoomId}");
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
        body = await GetAsync(admin, $"/analytics/load?from=2040-01-02&to=2040-01-02&roomId={scenario.RoomId}");
        Assert.AreEqual(1, body.GetProperty("items").GetArrayLength());
        body = await GetAsync(admin, "/analytics/load?from=2040-01-02&to=2040-01-03&roomId=9223372036854775807");
        Assert.AreEqual(0, body.GetProperty("items").GetArrayLength());
        body = await GetAsync(admin, $"/analytics/load?from=2040-01-10&to=2040-01-11&roomId={scenario.RoomId}");
        Assert.AreEqual(0, body.GetProperty("items").GetArrayLength());
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
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user, "admin");
        using var response = await client.GetAsync($"/analytics/load?{query}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(400, (await EnrollmentTestHost.JsonAsync(response)).GetProperty("status").GetInt32());
    }

    [TestMethod]
    public async Task Swagger_DeclaresRequiredTypedQueryDates()
    {
        await using var host = new EnrollmentTestHost();
        using var client = host.Client();
        var swagger = await GetAsync(client, "/swagger/v1/swagger.json");
        foreach (var (path, format) in new[] { ("/schedule", "date-time"), ("/analytics/load", "date") })
        {
            var parameters = swagger.GetProperty("paths").GetProperty(path).GetProperty("get")
                .GetProperty("parameters").EnumerateArray().ToArray();
            foreach (var name in new[] { "from", "to" })
            {
                var parameter = parameters.Single(x => x.GetProperty("name").GetString() == name);
                Assert.AreEqual("query", parameter.GetProperty("in").GetString());
                Assert.IsTrue(parameter.GetProperty("required").GetBoolean());
                Assert.AreEqual("string", parameter.GetProperty("schema").GetProperty("type").GetString());
                Assert.AreEqual(format, parameter.GetProperty("schema").GetProperty("format").GetString());
            }
        }
    }

    [TestMethod]
    public async Task ReadEndpoints_RequireAuthentication_AndLoadRequiresAdmin()
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var anonymous = host.Client();
        using var regular = host.Client(user);
        foreach (var path in new[] { "/schedule", "/analytics/load" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var forbidden = await regular.GetAsync("/analytics/load?from=2040-01-02&to=2040-01-03");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [TestMethod]
    public async Task NewEndpoints_ShareRateLimit_WithExistingEndpoints()
    {
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user, "admin");
        for (var i = 0; i < 60; i++)
        {
            using var response = (i % 5) switch
            {
                0 => await client.GetAsync("/enrollments/9223372036854775807"),
                1 => await host.SendAsync(user, "/enrollments/9223372036854775807/seats", "{\"seats\":1}"),
                2 => await client.GetAsync(ScheduleUrl(long.MaxValue)),
                3 => await client.GetAsync("/analytics/load?from=2040-01-02&to=2040-01-03&roomId=9223372036854775807"),
                _ => await host.CancelBookingAsync(user, long.MaxValue)
            };
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
        using var limited = await client.GetAsync(ScheduleUrl(long.MaxValue));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
