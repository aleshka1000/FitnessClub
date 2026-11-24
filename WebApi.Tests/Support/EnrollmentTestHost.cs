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

internal sealed class EnrollmentApplicationFactory(ManualTimeProvider clock, IInterceptor? interceptor)
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

internal sealed record EnrollmentScenario(
    long UserId,
    long ClassId,
    long RoomId,
    int Visits,
    uint ClassVersion,
    uint PassVersion);

internal sealed class EnrollmentTestHost : IAsyncDisposable
{
    public ManualTimeProvider Clock { get; } = new();

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

    public async Task<EnrollmentScenario> ScenarioAsync(
        int capacity = 4,
        int visits = 10,
        TimeSpan? startsIn = null,
        bool cancelled = false,
        DateOnly? validUntil = null)
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
        return PostJsonAsync(
            userId,
            JsonSerializer.Serialize(new { classId, seats }),
            key ?? Guid.NewGuid().ToString("N"));
    }

    public async Task<HttpResponseMessage> PostJsonAsync(long? userId, string? json, string? key)
    => await SendAsync(userId, "/enrollments", json, key);

    public Task<HttpResponseMessage> JoinAsync(long userId, long classId, int seats = 1, string? key = null) =>
        SendAsync(userId, "/enrollments/waitlist", JsonSerializer.Serialize(new { classId, seats }),
            key ?? Guid.NewGuid().ToString("N"));

    public Task<HttpResponseMessage> LeaveAsync(long userId, long id) =>
        SendAsync(userId, $"/enrollments/waitlist/{id}/cancel");

    public Task<HttpResponseMessage> CancelBookingAsync(long userId, long id) =>
        SendAsync(userId, $"/enrollments/{id}/cancel");

    public Task<HttpResponseMessage> ChangeSeatsAsync(long userId, long id, int seats) =>
        SendAsync(userId, $"/enrollments/{id}/seats", JsonSerializer.Serialize(new { seats }));

    public async Task<JsonElement> GetCardAsync(long userId, long id)
    {
        using var client = Client(userId);

        return await ApiAssertions.GetJsonAsync(client, $"/enrollments/{id}");
    }

    public Task<long> AddScheduledClassAsync(
        long roomId,
        DateTime startsAt,
        int capacity = 10,
        bool cancelled = false) =>
        DbAsync(async db =>
        {
            var scheduledClass = new Class
            {
                RoomId = roomId,
                Title = "Query test",
                StartsAt = startsAt,
                Capacity = capacity,
                DurationMinutes = 60,
                IsCancelled = cancelled
            };
            db.Classes.Add(scheduledClass);
            await db.SaveChangesAsync();

            return scheduledClass.Id;
        });

    public Task<string> GetClassStateAsync(long classId, params long[] additionalUsers) => DbAsync(async db =>
        {
            var scheduledClass = await db.Classes.IgnoreQueryFilters().Where(x => x.Id == classId)
                .Select(x => new
                {
                    x.Id,
                    x.Version,
                    x.Capacity,
                    x.StartsAt,
                    x.DurationMinutes,
                    x.IsCancelled,
                    x.RoomId,
                    x.Title
                })
                .SingleAsync();
            var bookings = await db.Enrollments.Where(x => x.ClassId == classId).OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.ClassId,
                    x.Seats,
                    x.Status,
                    x.CreatedAt,
                    x.CancelledAt,
                    x.Version
                }).ToListAsync();
            var waiting = await db.WaitlistEntries.Where(x => x.ClassId == classId).OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.Seats,
                    x.Status,
                    x.CreatedAt,
                    x.FinishedAt,
                    x.SkipReason,
                    x.PromotedEnrollmentId,
                    x.Version
                })
                .ToListAsync();
            var users = bookings.Select(x => x.UserId).Concat(waiting.Select(x => x.UserId))
                .Concat(additionalUsers)
                .Distinct()
                .ToArray();
            var passes = await db.Passes.Where(x => users.Contains(x.UserId)).OrderBy(x => x.UserId)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.Version,
                    x.RemainingVisits,
                    x.ValidUntil
                }).ToListAsync();
            var keys = await db.IdempotencyRequests.Where(x => x.ClassId == classId).OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.UserId,
                    x.Operation,
                    x.OriginalKey,
                    x.ClassId,
                    x.Seats,
                    x.EnrollmentId,
                    x.WaitlistEntryId,
                    x.CreatedAt
                })
                .ToListAsync();

            return JsonSerializer.Serialize(new { scheduledClass, bookings, waiting, passes, keys });
        });

    public async Task<HttpResponseMessage> SendAsync(long? userId, string path, string? json = null, string? key = null)
    {
        using var client = Client(userId);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

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
