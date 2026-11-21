using StackExchange.Redis;

namespace WebApi.RateLimiting;

public sealed class RedisUserRateLimiter(IConnectionMultiplexer connection, string keyPrefix) : IUserRateLimiter
{
    // решение: скользящая минута и время Redis дают общий атомарный лимит всем экземплярам API.
    private const string Script = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 60000)
        if redis.call('ZCARD', KEYS[1]) >= 60 then
            return 0
        end
        redis.call('ZADD', KEYS[1], now, ARGV[1])
        redis.call('PEXPIRE', KEYS[1], 60000)
        return 1
        """;

    public async Task<bool> TryAcquireAsync(long userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await connection.GetDatabase().ScriptEvaluateAsync(Script,
            new RedisKey[] { $"{keyPrefix}:{userId}" }, new RedisValue[] { Guid.NewGuid().ToString("N") });
        return (long)result == 1;
    }
}
