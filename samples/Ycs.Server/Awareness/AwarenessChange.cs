namespace Ycs.Server.Awareness;

/// <summary>
/// The client ids an update or a removal touched, as y-protocols' <c>update</c> event lists them:
/// <see cref="Updated"/> holds every client whose state arrived with a newer clock, equal or not;
/// <see cref="Changed"/> holds those of them whose state differs (y-protocols' <c>change</c> event).
/// y-websocket's server broadcasts <see cref="Added"/>, <see cref="Updated"/> and <see cref="Removed"/>.
/// </summary>
internal sealed record AwarenessChange(
    IReadOnlyList<ulong> Added,
    IReadOnlyList<ulong> Updated,
    IReadOnlyList<ulong> Changed,
    IReadOnlyList<ulong> Removed)
{
    public static readonly AwarenessChange None = new([], [], [], []);

    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Removed.Count == 0;

    /// <summary>Added, updated and removed ids: what the room broadcasts.</summary>
    public ulong[] Broadcast() => [.. Added, .. Updated, .. Removed];
}
