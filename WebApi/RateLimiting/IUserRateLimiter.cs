namespace WebApi.RateLimiting;

public interface IUserRateLimiter
{
    Task<bool> TryAcquireAsync(long userId, CancellationToken ct = default);
}
