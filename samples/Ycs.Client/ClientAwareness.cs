using Ycs.Server.Awareness;

namespace Ycs.Client;

/// <summary>
/// The client half of y-protocols/awareness: this client's own state with the clock rules y-protocols keeps for its
/// local state, and the remote states the server relays, in the server's <see cref="Server.Awareness.Awareness"/>.
/// Each method that changes the own state returns the awareness message to send. Not thread-safe: the provider calls
/// every member under its gate.
/// </summary>
internal sealed class ClientAwareness(ulong clientId, TimeProvider time)
{
    /// <summary>How long after its last message the own state is sent again (y-protocols: half the 30 s timeout).</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromMilliseconds(Server.Awareness.Awareness.OutdatedTimeoutMs / 2);

    // The server echoes the own state back like any other, so the remote states may hold it: Remote leaves it out.
    private Server.Awareness.Awareness remote = new(time);
    private ulong clock;
    private DateTimeOffset sent;

    public ulong ClientId => clientId;

    /// <summary>This client's state as JSON text; null when it has none (it left, or never set one).</summary>
    public string? LocalState { get; private set; }

    /// <summary>The other clients that have a state, and their states as JSON text.</summary>
    public IEnumerable<(ulong ClientId, string State)> Remote
    {
        get
        {
            foreach (var id in remote.ClientIds)
            {
                if (id != clientId && remote.TryGetState(id, out var state))
                {
                    yield return (id, state);
                }
            }
        }
    }

    /// <summary>Sets the own state (null to leave) with the next clock, as y-protocols' <c>setLocalState</c>.</summary>
    public byte[] SetLocalState(string? json)
    {
        LocalState = json;
        clock++;
        sent = time.GetUtcNow();
        return Wire.AwarenessMessage(clientId, clock, json);
    }

    /// <summary>
    /// The own state again with the next clock if it is due for renewal, as y-protocols' timer renews it; null when
    /// there is no state or it is not due.
    /// </summary>
    public byte[]? RenewIfDue() =>
        LocalState is not null && time.GetUtcNow() - sent >= RenewAfter ? SetLocalState(LocalState) : null;

    /// <summary>
    /// Applies an awareness update from the server; returns what changed among the remote states, and the own state
    /// to send again when the update removed it. Throws <see cref="YUpdateException"/> for a malformed update.
    /// </summary>
    public (AwarenessChange Change, byte[]? Reply) ApplyUpdate(ReadOnlySpan<byte> update)
    {
        var change = remote.ApplyUpdate(update);

        // As y-protocols: a removal of the own state (the server timed it out) is answered with a newer clock, so that
        // the others keep this client.
        byte[]? reply = null;
        if (LocalState is not null && remote.TryGetClock(clientId, out var removedAt) && removedAt >= clock && !remote.TryGetState(clientId, out _))
        {
            clock = removedAt;
            reply = SetLocalState(LocalState);
        }

        return (change, reply);
    }

    /// <summary>Removes the remote states that were not renewed within the 30 s timeout.</summary>
    public AwarenessChange RemoveOutdated() => remote.RemoveOutdated();

    /// <summary>
    /// Forgets every remote state, as y-websocket's provider does when its connection closes, and their clocks: the
    /// next connection starts with the server's full state, whose unchanged states come with the clocks seen before.
    /// </summary>
    public AwarenessChange Forget()
    {
        var change = remote.RemoveStates([.. remote.ClientIds]);
        remote = new Server.Awareness.Awareness(time);
        return change;
    }
}
