namespace BasitBilgisayarAraclari.Tests.Fakes;

internal sealed class FakeTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);

    public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
}
