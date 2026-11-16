namespace Ycs.Client.Tests.Support;

/// <summary>A clock that moves only when told; no timers.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    public void Advance(TimeSpan by) => now += by;

    public override DateTimeOffset GetUtcNow() => now;
}
