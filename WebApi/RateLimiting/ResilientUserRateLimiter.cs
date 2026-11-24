using StackExchange.Redis;

namespace WebApi.RateLimiting;

public sealed class ResilientUserRateLimiter(
    IUserRateLimiter primary,
    InMemoryFallbackRateLimiter fallback,
    ILogger<ResilientUserRateLimiter> logger) : IUserRateLimiter
{
    private int _usingFallback;

    public async Task<bool> TryAcquireAsync(long userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var allowed = await primary.TryAcquireAsync(userId, ct);

            if (Interlocked.Exchange(ref _usingFallback, 0) != 0)
                logger.LogInformation("Redis rate limiting recovered; the shared limit is active again.");

            return allowed;
        }
        catch (Exception exception) when (exception is RedisConnectionException or RedisTimeoutException)
        {
            ct.ThrowIfCancellationRequested();

            // решение: при сбое Redis используем общий резерв: 10 запросов за скользящую секунду на экземпляр API.
            // Это защита нагрузки без синхронизации с Redis, вместо обычного лимита на пользователя.
            if (Interlocked.Exchange(ref _usingFallback, 1) == 0)
                logger.LogWarning(
                    exception,
                    "Redis rate limiting unavailable; using the shared fallback limit of 10 requests per second on this API instance.");

            return await fallback.TryAcquireAsync(userId, ct);
        }
    }
}
