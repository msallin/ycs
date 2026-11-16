using System;

namespace Ycs;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
