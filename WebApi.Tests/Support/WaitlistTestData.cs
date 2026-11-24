namespace WebApi.Tests;

internal static class WaitlistTestData
{
    public static async Task<(EnrollmentScenario Scenario, long BookingId)> FullAsync(
        EnrollmentTestHost host,
        int capacity = 2,
        int visits = 20)
    {
        var scenario = await host.ScenarioAsync(capacity, visits);
        using var booked = await host.PostAsync(scenario.UserId, scenario.ClassId, capacity, "booking-key");

        return (scenario, await ApiAssertions.CreatedIdAsync(booked));
    }
}
