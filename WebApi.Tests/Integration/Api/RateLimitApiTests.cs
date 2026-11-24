using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace WebApi.Tests;

[TestClass]
public class RateLimitApiTests
{
    [TestMethod]
    public async Task SharedLimit_CountsAllEightRoutes_AndIsScopedToUser()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();
        using var client = host.Client(user, "admin");
        var endpoints = new[]
        {
            (HttpMethod.Post, "/enrollments", "{\"classId\":9223372036854775807,\"seats\":1}", HttpStatusCode.NotFound),
            (HttpMethod.Post, "/enrollments/9223372036854775807/seats", "{\"seats\":1}", HttpStatusCode.NotFound),
            (HttpMethod.Post, "/enrollments/9223372036854775807/cancel", (string?)null, HttpStatusCode.NotFound),
            (HttpMethod.Get, "/enrollments/9223372036854775807", null, HttpStatusCode.NotFound),
            (HttpMethod.Get, "/schedule?from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z&roomId=9223372036854775807", null, HttpStatusCode.OK),
            (HttpMethod.Get, "/analytics/load?from=2040-01-02&to=2040-01-03&roomId=9223372036854775807", null, HttpStatusCode.OK),
            (HttpMethod.Post, "/enrollments/waitlist", "{\"classId\":9223372036854775807,\"seats\":1}", HttpStatusCode.NotFound),
            (HttpMethod.Post, "/enrollments/waitlist/9223372036854775807/cancel", null, HttpStatusCode.NotFound)
        };

        for (var i = 0; i < 60; i++)
        {
            var (method, path, body, expected) = endpoints[i % endpoints.Length];
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Idempotency-Key", $"limit-{i}");

            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            // Act
            using var response = await client.SendAsync(request);

            // Assert
            Assert.AreEqual(
                expected,
                response.StatusCode,
                $"Request {i + 1}: {await response.Content.ReadAsStringAsync()}");
        }

        // Act
        using var limited = await client.GetAsync("/enrollments/9223372036854775807");

        // Assert
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual(429, (await ApiAssertions.JsonAsync(limited)).GetProperty("status").GetInt32());

        // Arrange
        using var other = host.Client(await host.AddUserAsync());

        // Act
        using var independent = await other.GetAsync("/enrollments/9223372036854775807");

        // Assert
        Assert.AreEqual(HttpStatusCode.NotFound, independent.StatusCode);
        Assert.AreEqual(0, await host.DbAsync(db => db.IdempotencyRequests.CountAsync(x => x.UserId == user)));
    }

    [TestMethod]
    public async Task HealthAndLogin_AreAvailableAfterUserQuotaIsExhausted()
    {
        // Arrange
        await using var host = new EnrollmentTestHost();
        var user = await host.AddUserAsync();

        for (var i = 0; i < 60; i++)
        {
            using var response = await host.CancelBookingAsync(user, long.MaxValue);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var client = host.Client(user);

        // Act
        using var limited = await client.GetAsync("/enrollments/9223372036854775807");

        // Assert
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Act
        using var health = await client.GetAsync("/health");

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);

        // Act
        using var login = await client.PostAsync("/auth/login",
            new StringContent(
                "{\"email\":\"user001@club.test\",\"password\":\"Password123!\"}",
                Encoding.UTF8,
                "application/json"));

        // Assert
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
    }
}
