namespace Ycs.Server.Awareness;

internal sealed class AwarenessClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
