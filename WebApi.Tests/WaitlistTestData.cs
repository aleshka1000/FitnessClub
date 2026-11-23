using System.Net;

namespace WebApi.Tests;

internal static class WaitlistTestData
{
    public static async Task<long> IdAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await EnrollmentTestHost.JsonAsync(response)).GetProperty("id").GetInt64();
    }

    public static async Task AssertReasonAsync(HttpResponseMessage response, string reason)
    {
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await EnrollmentTestHost.JsonAsync(response);
        Assert.AreEqual(409, body.GetProperty("status").GetInt32());
        Assert.AreEqual(reason, body.GetProperty("reason").GetString());
    }

    public static async Task<(EnrollmentScenario Scenario, long BookingId)> FullAsync(EnrollmentTestHost host,
        int capacity = 2, int visits = 20)
    {
        var scenario = await host.ScenarioAsync(capacity, visits);
        using var booked = await host.PostAsync(scenario.UserId, scenario.ClassId, capacity, "booking-key");
        return (scenario, await IdAsync(booked));
    }
}
