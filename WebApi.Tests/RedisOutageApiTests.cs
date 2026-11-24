using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DataAccessLayer.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using WebApi.Auth;

namespace WebApi.Tests;

[TestClass]
public class RedisOutageApiTests
{
    [TestMethod]
    public async Task StartupWithoutRedis_ServesDatabaseCards_AndSharesLocalLimitAcrossEndpoints()
    {
        await using var host = new EnrollmentTestHost();
        var scenario = await host.ScenarioAsync();
        using var created = await host.PostAsync(scenario.UserId, scenario.ClassId);
        var id = await WaitlistTestData.IdAsync(created);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unavailablePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var clock = new OutageTestTimeProvider(host.Clock.Now);
        await using var offlineFactory = host.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = $"127.0.0.1:{unavailablePort},connectRetry=0,connectTimeout=100,asyncTimeout=100"
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        });

        using var client = offlineFactory.CreateClient();
        var connection = offlineFactory.Services.GetRequiredService<IConnectionMultiplexer>();
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(unavailablePort, ((IPEndPoint)connection.GetEndPoints().Single()).Port);
        var token = offlineFactory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = scenario.UserId, Email = "test@club.test", Role = "admin" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var endpoints = new[]
        {
            (HttpMethod.Get, $"/enrollments/{id}", (string?)null, HttpStatusCode.OK),
            (HttpMethod.Get, "/schedule?from=2040-01-02T00:00:00Z&to=2040-01-03T00:00:00Z", null, HttpStatusCode.OK),
            (HttpMethod.Get, "/analytics/load?from=2040-01-02&to=2040-01-03", null, HttpStatusCode.OK),
            (HttpMethod.Post, $"/enrollments/{id}/seats", "{\"seats\":1}", HttpStatusCode.OK),
            (HttpMethod.Post, "/enrollments/9223372036854775807/cancel", null, HttpStatusCode.NotFound),
            (HttpMethod.Post, "/enrollments", "{}", HttpStatusCode.BadRequest),
            (HttpMethod.Post, "/enrollments/waitlist", "{}", HttpStatusCode.BadRequest),
            (HttpMethod.Post, "/enrollments/waitlist/9223372036854775807/cancel", null, HttpStatusCode.NotFound)
        };
        for (var i = 0; i < 10; i++)
        {
            var (method, path, json, expected) = endpoints[i % endpoints.Length];
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            if (i == 0)
            {
                var card = await EnrollmentTestHost.JsonAsync(response);
                Assert.AreEqual(id, card.GetProperty("id").GetInt64());
                Assert.AreEqual(9, card.GetProperty("pass").GetProperty("remainingVisits").GetInt32());
            }
        }

        using var rejected = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.AreEqual(429, (await EnrollmentTestHost.JsonAsync(rejected)).GetProperty("status").GetInt32());
        var otherUser = await host.AddUserAsync();
        var otherToken = offlineFactory.Services.GetRequiredService<AccessTokenService>()
            .Create(new User { Id = otherUser, Email = "other@club.test", Role = "user" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken.AccessToken);
        using var alsoRejected = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, alsoRejected.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var allowed = await client.GetAsync($"/enrollments/{id}");
        Assert.AreEqual(HttpStatusCode.NotFound, allowed.StatusCode);
    }

    private sealed class OutageTestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => now.AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
