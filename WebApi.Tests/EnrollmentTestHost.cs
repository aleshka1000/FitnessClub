using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Auth;

namespace WebApi.Tests;

internal sealed class FixedTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class EnrollmentApplicationFactory(FixedTimeProvider clock, IInterceptor? interceptor)
    : ApiApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);

            if (interceptor is not null)
            {
                services.AddDbContext<ClubDbContext>(options => options.AddInterceptors(interceptor));
            }
        });
    }
}

internal sealed record EnrollmentScenario(long UserId, long ClassId, long RoomId, int Visits,
    uint ClassVersion, uint PassVersion);

internal sealed class EnrollmentTestHost : IAsyncDisposable
{
    public FixedTimeProvider Clock { get; } = new();

    public WebApplicationFactory<Program> Factory { get; }

    public EnrollmentTestHost(IInterceptor? interceptor = null)
    {
        Factory = new EnrollmentApplicationFactory(Clock, interceptor);
    }

    public async Task<T> DbAsync<T>(Func<ClubDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ClubDbContext>());
    }

    public async Task<long> AddUserAsync(int visits = 10, DateOnly? validUntil = null)
    {
        return await DbAsync(async db =>
        {
            var user = new User
            {
                Email = $"test-{Guid.NewGuid():N}@club.test",
                PasswordHash = "unused",
                Role = "user",
                CreatedAt = Clock.Now.UtcDateTime
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.Passes.Add(new Pass
            {
                UserId = user.Id,
                RemainingVisits = visits,
                ValidUntil = validUntil ?? DateOnly.FromDateTime(Clock.Now.UtcDateTime).AddDays(30)
            });
            await db.SaveChangesAsync();
            return user.Id;
        });
    }

    public async Task<long> AddClassAsync(int capacity = 4, TimeSpan? startsIn = null, bool cancelled = false)
    {
        return await DbAsync(async db =>
        {
            var scheduledClass = new Class
            {
                Room = new Room { Name = $"Test room {Guid.NewGuid():N}" },
                Title = "Test class",
                StartsAt = Clock.Now.UtcDateTime.Add(startsIn ?? TimeSpan.FromDays(1)),
                DurationMinutes = 60,
                Capacity = capacity,
                IsCancelled = cancelled
            };
            db.Classes.Add(scheduledClass);
            await db.SaveChangesAsync();
            return scheduledClass.Id;
        });
    }

    public async Task<EnrollmentScenario> ScenarioAsync(int capacity = 4, int visits = 10,
        TimeSpan? startsIn = null, bool cancelled = false, DateOnly? validUntil = null)
    {
        var userId = await AddUserAsync(visits, validUntil);
        var classId = await AddClassAsync(capacity, startsIn, cancelled);
        return await DbAsync(async db =>
        {
            var scheduledClass = await db.Classes.IgnoreQueryFilters().SingleAsync(x => x.Id == classId);
            var pass = await db.Passes.SingleAsync(x => x.UserId == userId);
            return new EnrollmentScenario(userId, classId, scheduledClass.RoomId, visits,
                scheduledClass.Version, pass.Version);
        });
    }

    public HttpClient Client(long? userId = null, string role = "user")
    {
        var client = Factory.CreateClient();

        if (userId is not null)
        {
            var token = Factory.Services.GetRequiredService<AccessTokenService>()
                .Create(new User { Id = userId.Value, Email = "test@club.test", Role = role });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        }

        return client;
    }

    public Task<HttpResponseMessage> PostAsync(long userId, long classId, int seats = 1, string? key = null)
    {
        return PostJsonAsync(userId, JsonSerializer.Serialize(new { classId, seats }), key ?? Guid.NewGuid().ToString("N"));
    }

    public async Task<HttpResponseMessage> PostJsonAsync(long? userId, string? json, string? key)
    {
        using var client = Client(userId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/enrollments");

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public async Task AssertUnchangedAsync(EnrollmentScenario scenario)
    {
        await DbAsync(async db =>
        {
            var pass = await db.Passes.SingleAsync(x => x.UserId == scenario.UserId);
            Assert.AreEqual(scenario.Visits, pass.RemainingVisits);
            Assert.AreEqual(scenario.PassVersion, pass.Version);
            Assert.AreEqual(scenario.ClassVersion, await db.Classes.IgnoreQueryFilters()
                .Where(x => x.Id == scenario.ClassId).Select(x => x.Version).SingleAsync());
            Assert.AreEqual(0, await db.Enrollments.CountAsync(x => x.UserId == scenario.UserId));
            Assert.AreEqual(0, await db.IdempotencyRequests.CountAsync(x => x.UserId == scenario.UserId));
            return true;
        });
    }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
