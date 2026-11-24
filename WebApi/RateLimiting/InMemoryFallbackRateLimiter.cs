namespace WebApi.RateLimiting;

public sealed class InMemoryFallbackRateLimiter(TimeProvider timeProvider) : IUserRateLimiter
{
    private const int Limit = 10;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private readonly Queue<long> _requests = new();

    public Task<bool> TryAcquireAsync(long userId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            var now = timeProvider.GetTimestamp();
            while (_requests.TryPeek(out var oldest) && timeProvider.GetElapsedTime(oldest, now) >= Window)
                _requests.Dequeue();
            if (_requests.Count >= Limit) return Task.FromResult(false);

            _requests.Enqueue(now);
            return Task.FromResult(true);
        }
    }
}
