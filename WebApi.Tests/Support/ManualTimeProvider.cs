namespace WebApi.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset? initialTime = null) : TimeProvider
{
    private long _ticks;
    public DateTimeOffset Now { get; set; } = initialTime ?? new DateTimeOffset(2030, 1, 2, 12, 0, 0, TimeSpan.Zero);
    public TimeSpan UtcOffset { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => Now.Add(UtcOffset);

    public void Advance(TimeSpan duration)
    {
        Now = Now.Add(duration);
        Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
