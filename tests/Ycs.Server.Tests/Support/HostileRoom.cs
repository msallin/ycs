using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// One room, honest clients that edit through it, a listener that only receives, and hostile sessions that send
/// garbage, replays and valid but expensive updates, driven step by step from a seed. After every step everything
/// is drained and <see cref="Check"/> holds the invariants; a violation throws <see cref="HostileRoomException"/>
/// naming the seed, the step and the last steps taken.
/// </summary>
internal sealed class HostileRoom
{
    /// <summary>Small limits, so that a step reaches them cheaply; honest steps stay far below them.</summary>
    public static readonly UpdateLimits Limits = new()
    {
        MaxStructsPerUpdate = 500,
        MaxPendingStructs = 300,
        MaxPendingBytes = 16 * 1024,
        MaxIntegrationSteps = 10_000,
        MaxDepth = 32,
    };

    // What waits may reach twice the limits (docs/api.md, "pending data"); a v1 struct is larger than the v2 bytes
    // the limit counts by its client, clock and origins, at most about 30 bytes.
    public static readonly long MaxPendingExcess = 2 * (Limits.MaxPendingBytes + (30 * Limits.MaxPendingStructs));

    private const long HonestIds = 1L << 40;
    private const int MaxHostile = 3;

    private readonly ulong seed;
    private readonly HostileRandom rng;
    private readonly Server.Awareness.AwarenessClock clock = new();
    private readonly List<HostilePeer> honest = [];
    private readonly HostilePeer listener;
    private readonly List<Hostile> hostiles = [];
    private readonly Queue<string> log = new();
    private int step;
    private long nextForgerId = 1_000;

    public HostileRoom(ulong seed, int honestClients = 3)
    {
        this.seed = seed;
        rng = new HostileRandom(seed);
        Room = new Room("hostile", clock, Limits);
        listener = new HostilePeer(HonestIds - 1, clock);
        listener.Listen(Room);
        for (var i = 0; i < honestClients; i++)
        {
            var peer = new HostilePeer(HonestIds + i, clock);
            peer.Connect(Room);
            honest.Add(peer);
        }

        Drain();
    }

    public Room Room { get; }

    public HostileStats Stats { get; } = new();

    /// <summary>Runs <paramref name="steps"/> random steps, then reconnects everyone and checks again.</summary>
    public void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            Step();
        }

        foreach (var peer in honest.Where(p => !p.Connected))
        {
            peer.Connect(Room);
        }

        Act("reconnect all", () => { });
        Check(full: true);
        var fresh = new HostilePeer(HonestIds + 100, clock);
        fresh.Connect(Room);
        honest.Add(fresh);
        Act("fresh client", () => { });
        Check(full: true);
    }

    public void Step()
    {
        var roll = rng.Int(100);
        if (roll < 40)
        {
            HonestStep(rng.Pick(honest));
        }
        else if (roll < 42)
        {
            Act("clock +31 s, sweep", () =>
            {
                clock.Advance(TimeSpan.FromSeconds(31));
                Room.RemoveOutdatedAwareness();
            });
        }
        else
        {
            HostileStep();
        }
    }

    private void HonestStep(HostilePeer peer)
    {
        var roll = rng.Int(100);
        if (roll < 8)
        {
            Act($"honest {peer.ClientId} {(peer.Connected ? "disconnects" : "reconnects")}", () =>
            {
                if (peer.Connected)
                {
                    peer.Disconnect();
                }
                else
                {
                    peer.Connect(Room);
                }
            });
        }
        else if (roll < 18)
        {
            Act($"honest {peer.ClientId} announces", peer.Announce);
        }
        else
        {
            Act($"honest {peer.ClientId} edits", () => Edit(peer.Doc, hostile: false));
        }
    }

    // A small edit of the text, the map or the array, as a person makes one.
    private void Edit(YDoc doc, bool hostile)
    {
        var text = doc.GetText("text");
        switch (rng.Int(hostile ? 6 : 5))
        {
            case 0 or 1:
                text.Insert(rng.Int(text.Length + 1), rng.Text(rng.Between(1, 8), hostile));
                break;
            case 2 when text.Length > 0:
                var at = rng.Int(text.Length);
                text.Delete(at, Math.Min(text.Length - at, rng.Between(1, 6)));
                break;
            case 3:
                doc.GetMap("map").Set($"k{rng.Int(8)}", rng.Int(1_000));
                break;
            case 5:
                doc.GetMap("map").Set($"k{rng.Int(8)}", new YMap());
                break;
            default:
                var array = doc.GetArray("array");
                if (array.Length > 0 && rng.Chance(0.3))
                {
                    array.Delete(rng.Int(array.Length));
                }
                else
                {
                    array.Insert(rng.Int(array.Length + 1), [rng.Text(3, hostile)]);
                }

                break;
        }
    }

    private void HostileStep()
    {
        var open = hostiles.Where(h => !h.Session.IsClosed).ToList();
        if (open.Count == 0 || (open.Count < MaxHostile && rng.Chance(0.1)))
        {
            var capacity = rng.Chance(0.2) ? rng.Between(1, 8) : Session.DefaultQueueCapacity;
            Act($"hostile joins (queue {capacity})", () =>
            {
                var hostile = new Hostile(new Session(Room, capacity), Forger, drains: capacity > 8 || rng.Chance(0.5));
                hostiles.Add(hostile);
                hostile.Session.Start();
            });
            return;
        }

        var h = rng.Pick(open);
        var roll = rng.Int(100);
        if (roll < 4)
        {
            Act("hostile closes", h.Session.Close);
            return;
        }

        var (messages, kind) = HostileMessage(h, roll);
        if (messages.Length > 0)
        {
            var before = Room.EncodeStateVector();
            Act($"hostile sends {kind} ({string.Join(" + ", messages.Select(m => m.Length))} B)", () =>
            {
                foreach (var message in messages)
                {
                    h.Session.Receive(message);
                }
            });
            Stats.Sent(kind, h.Session.IsClosed, !before.AsSpan().SequenceEqual(Room.EncodeStateVector()));
        }
    }

    private YDoc Forger(YDoc? basedOn = null)
    {
        // Half of them start empty: everything they write is concurrent with the whole document.
        var forger = new YDoc(new YDocOptions { ClientId = nextForgerId++ });
        if (basedOn is not null || rng.Chance(0.5))
        {
            forger.ApplyUpdateV1(basedOn?.EncodeStateAsUpdateV1() ?? Room.EncodeStateAsUpdate(), null, UpdateLimits.Unlimited);
        }

        return forger;
    }

    private (byte[][] Messages, string Kind) HostileMessage(Hostile h, int roll)
    {
        switch (roll)
        {
            case < 20:
                {
                    // A valid edit, sent now or held back so that later ones wait for it.
                    var update = HostileUpdates.Edit(h.Forger, d => Edit(d, hostile: true));
                    if (rng.Chance(0.3))
                    {
                        h.Held.Add(update);
                        return ([], "");
                    }

                    return ([HostileMessages.Sync(rng.Chance(0.2) ? SyncProtocol.MessageYjsSyncStep2 : SyncProtocol.MessageYjsUpdate, update)], "valid update");
                }

            case < 26 when h.Held.Count > 0:
                {
                    var i = rng.Int(h.Held.Count);
                    var update = h.Held[i];
                    if (rng.Chance(0.7))
                    {
                        h.Held.RemoveAt(i);
                    }

                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, update)], "held update, out of order");
                }

            case < 45:
                {
                    var payload = h.Held.Count > 0 && rng.Chance(0.3)
                        ? rng.Pick(h.Held)
                        : HostileUpdates.Edit(h.Forger, d => Edit(d, hostile: true));
                    var (message, kind) = HostileMessages.Mutate(rng, rng.Pick<ulong>([0, 1, 2]), payload);
                    return ([message], $"mutated update: {kind}");
                }

            case < 52 when h.Corpus.Count > 0:
                {
                    var message = rng.Pick(h.Corpus);
                    return ([message], $"replay of a message of type {Messages.Split(message).Type}");
                }

            case < 57:
                {
                    var client = rng.Int(3) switch
                    {
                        0 => (long)rng.Pick(honest).ClientId,
                        1 => h.Forger.ClientId,
                        _ => rng.Between(1, 900),
                    };
                    var update = HostileUpdates.Delete(client, rng.Int(200), rng.Between(1, 50));
                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, update)], $"delete of {client}'s range, maybe unknown");
                }

            case < 62:
                {
                    var vector = rng.Chance(0.5) ? h.Forger.EncodeStateVectorV1() : rng.Bytes(rng.Int(24));
                    return rng.Chance(0.5)
                        ? ([HostileMessages.Sync(SyncProtocol.MessageYjsSyncStep1, vector)], "step 1")
                        : ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, vector)], "state vector as an update");
                }

            case < 66:
                return Expensive(h);

            case < 72:
                {
                    // Resynchronise the forger now and then, so that its edits are not all concurrent with everything.
                    h.Resync();
                    return ([], "");
                }

            default:
                {
                    var (message, kind) = HostileMessages.Awareness(rng, [.. honest.Select(p => p.ClientId)]);
                    return ([message], kind);
                }
        }
    }

    // Valid updates that cost: many structs, conflicting runs, deep nesting, long strings.
    private (byte[][] Messages, string Kind) Expensive(Hostile h)
    {
        var limit = (int)Limits.MaxStructsPerUpdate;
        switch (rng.Int(5))
        {
            case 4:
                {
                    // Updates that all wait for one never sent, until what waits is beyond the limits.
                    HostileUpdates.Edit(h.Forger, d => d.GetText("text").Insert(0, "never sent"));
                    var updates = new List<byte[]>();
                    for (var i = rng.Between(2, 12); i > 0; i--)
                    {
                        updates.Add(HostileUpdates.Edit(h.Forger, d =>
                        {
                            var text = d.GetText("text");
                            if (rng.Chance(0.5))
                            {
                                text.Insert(rng.Int(text.Length + 1), rng.Text(rng.Between(500, 6_000), hostile: true));
                            }
                            else
                            {
                                for (var n = rng.Between(50, 200); n > 0; n--)
                                {
                                    text.Insert(0, "p");
                                }
                            }
                        }));
                    }

                    return ([.. updates.Select(u => HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, u))], $"{updates.Count} updates waiting");
                }

            case 0:
                {
                    // Typed backwards: every character its own struct.
                    var n = rng.Between(limit / 2, limit * 2);
                    var update = HostileUpdates.Edit(h.Forger, d =>
                    {
                        var text = d.GetText("text");
                        var at = rng.Int(text.Length + 1);
                        for (var i = 0; i < n; i++)
                        {
                            text.Insert(at, "x");
                        }
                    });
                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, update)], $"{n} structs");
                }

            case 1:
                {
                    // n and m typed backwards by two clients at one place: n * m integration steps.
                    var other = Forger(h.Forger);
                    var at = rng.Int(h.Forger.GetText("text").Length + 1);
                    var n = rng.Between(40, 160);
                    var m = rng.Between(40, 160);
                    var forward = HostileUpdates.Edit(h.Forger, d =>
                    {
                        for (var i = 0; i < n; i++)
                        {
                            d.GetText("text").Insert(at, "f");
                        }
                    });
                    var backward = HostileUpdates.Edit(other, d =>
                    {
                        for (var i = 0; i < m; i++)
                        {
                            d.GetText("text").Insert(at, "b");
                        }
                    });
                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, forward), HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, backward)], $"conflict {n} x {m}");
                }

            case 2:
                {
                    var depth = rng.Between(Limits.MaxDepth / 2, Limits.MaxDepth * 2);
                    var asTypes = rng.Chance(0.5);
                    var update = HostileUpdates.Edit(h.Forger, d =>
                    {
                        if (asTypes)
                        {
                            var array = d.GetArray("array");
                            for (var i = 0; i < depth; i++)
                            {
                                var inner = new YArray();
                                array.Insert(0, [inner]);
                                array = inner;
                            }
                        }
                        else
                        {
                            object value = 1;
                            for (var i = 0; i < depth; i++)
                            {
                                value = new List<object> { value };
                            }

                            d.GetArray("array").Insert(0, [value]);
                        }
                    });
                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, update)], $"{(asTypes ? "types" : "values")} {depth} deep");
                }

            default:
                {
                    var length = rng.Between(1_000, 20_000);
                    var update = HostileUpdates.Edit(h.Forger, d => d.GetText("text").Insert(0, rng.Text(length, hostile: true)));
                    return ([HostileMessages.Sync(SyncProtocol.MessageYjsUpdate, update)], $"string of {length}");
                }
        }
    }

    // Runs one step: any exception out of the room is a failure; then drains and checks.
    private void Act(string what, Action action)
    {
        step++;
        log.Enqueue($"{step}: {what}");
        if (log.Count > 12)
        {
            log.Dequeue();
        }

        var closedBefore = hostiles.Where(h => h.Session.IsClosed).Select(h => h.Session).ToHashSet();
        try
        {
            action();
        }
        catch (Exception e)
        {
            throw Fail($"the step threw {e.GetType().Name}: {e.Message}", e);
        }

        foreach (var h in hostiles.Where(h => h.Session.IsClosed && !closedBefore.Contains(h.Session)))
        {
            Stats.Closed(h.Session.CloseReason);
        }

        Drain();
        Check(full: step % 32 == 0);
    }

    private void Drain()
    {
        bool any;
        do
        {
            any = listener.Pump() > 0;
            foreach (var peer in honest)
            {
                any |= peer.Pump() > 0;
            }

            foreach (var h in hostiles.Where(h => h.Drains))
            {
                while (h.Session.Outgoing.TryRead(out var message))
                {
                    if (h.Corpus.Count < 64)
                    {
                        h.Corpus.Add(message);
                    }
                    else
                    {
                        h.Corpus[rng.Int(64)] = message;
                    }
                }
            }
        }
        while (any);
    }

    /// <summary>
    /// The invariants after a drained step; the content and what the room holds beyond it only when
    /// <paramref name="full"/>, as they cost more than the steps (a divergence of content stays until then).
    /// </summary>
    public void Check(bool full)
    {
        if (!listener.Connected || honest.Any(p => p.Session is { IsClosed: true }))
        {
            throw Fail("an honest session closed: " + string.Join(", ", honest.Select(p => p.Session?.CloseReason)) + $", listener {listener.Session!.CloseReason}");
        }

        foreach (var h in hostiles.Where(h => h.Session.IsClosed))
        {
            if (h.Session.CloseReason is not (SessionCloseReason.ProtocolError or SessionCloseReason.QueueFull or SessionCloseReason.Disconnected))
            {
                throw Fail($"a hostile session closed with {h.Session.CloseReason}");
            }
        }

        var vector = Vector(Room.EncodeStateVector());
        foreach (var peer in honest.Where(p => p.Connected).Append(listener))
        {
            if (Vector(peer.Doc.EncodeStateVectorV1()) != vector)
            {
                throw Fail($"the state vector of {peer.ClientId}, {Vector(peer.Doc.EncodeStateVectorV1())}, is not the room's, {vector}");
            }
        }

        CheckAwareness();
        if (!full)
        {
            return;
        }

        var expected = Signature(listener.Doc);
        foreach (var peer in honest.Where(p => p.Connected))
        {
            var actual = Signature(peer.Doc);
            if (actual != expected)
            {
                throw Fail($"honest {peer.ClientId} diverged from what the room broadcast:\n  {actual}\n  {expected}");
            }
        }

        var roomState = Room.EncodeStateAsUpdate();
        var fromRoom = new YDoc();
        fromRoom.ApplyUpdateV1(roomState, null, UpdateLimits.Unlimited);
        if (Signature(fromRoom) != expected)
        {
            throw Fail($"the room's state diverged from what it broadcast:\n  {Signature(fromRoom)}\n  {expected}");
        }

        var excess = roomState.Length - listener.Doc.EncodeStateAsUpdateV1().Length;
        Stats.PendingExcess(excess);
        if (excess > MaxPendingExcess)
        {
            throw Fail($"the room holds {excess} B more than it integrated");
        }
    }

    private void CheckAwareness()
    {
        var states = listener.States();
        var roomIds = string.Join(';', Room.AwarenessClientIds.Order());
        var listenerIds = string.Join(';', listener.View.ClientIds.Order());
        if (roomIds != listenerIds)
        {
            throw Fail($"the room's awareness ids {roomIds} are not the broadcast ones {listenerIds}");
        }

        foreach (var peer in honest.Where(p => p.Connected))
        {
            if (peer.States() != states)
            {
                throw Fail($"honest {peer.ClientId} sees awareness {peer.States()}, the room broadcast {states}");
            }
        }

        var controlled = honest.Where(p => p.Connected).Select(p => p.Session!)
            .Concat(hostiles.Where(h => !h.Session.IsClosed).Select(h => h.Session))
            .SelectMany(s => s.ControlledIds)
            .ToHashSet();
        var orphans = Room.AwarenessClientIds.Where(id => !controlled.Contains(id)).ToList();
        if (orphans.Count > 0)
        {
            throw Fail($"awareness shows states no open session set: {string.Join(", ", orphans)}");
        }

        Stats.AwarenessIds(Room.AwarenessClientIds.Count);
    }

    // The state vector as sorted text, the deleted ranges, and the content of the types the clients edit.
    private static string Signature(YDoc doc)
    {
        string Safe(Func<object> read)
        {
            try
            {
                return Canonical(read());
            }
            catch (Exception e)
            {
                return $"<{e.GetType().Name}>";
            }
        }

        var snapshot = doc.CreateSnapshot();
        return $"sv {Vector(doc.EncodeStateVectorV1())} ds {Convert.ToHexString(snapshot.EncodeSnapshot())} text {Safe(() => doc.GetText("text").ToString())} map {Safe(() => doc.GetMap("map").ToJson())} array {Safe(() => doc.GetArray("array").ToJson())}";
    }

    private static string Vector(byte[] encoded)
    {
        var reader = new MemoryStream(encoded);
        var count = ReadVarUint(reader);
        var entries = new List<(ulong Client, ulong Clock)>();
        for (ulong i = 0; i < count; i++)
        {
            entries.Add((ReadVarUint(reader), ReadVarUint(reader)));
        }

        return string.Join(',', entries.Where(e => e.Clock > 0).OrderBy(e => e.Client).Select(e => $"{e.Client}:{e.Clock}"));
    }

    private static ulong ReadVarUint(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = stream.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }

    private static string Canonical(object? value) => value switch
    {
        null => "null",
        string s => System.Text.Json.JsonSerializer.Serialize(s),
        IDictionary<string, object> map => "{" + string.Join(',', map.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}:{Canonical(e.Value)}")) + "}",
        System.Collections.IEnumerable list and not byte[] => "[" + string.Join(',', list.Cast<object?>().Select(Canonical)) + "]",
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    private HostileRoomException Fail(string what, Exception? inner = null) =>
        new($"seed {seed}, step {step}: {what}\nlast steps:\n  {string.Join("\n  ", log)}", inner);

    // The forger is made on first use: most hostile sessions close before they need one.
    private sealed class Hostile(Session session, Func<YDoc?, YDoc> newForger, bool drains)
    {
        private YDoc? forger;

        public Session Session { get; } = session;

        public YDoc Forger => forger ??= newForger(null);

        public void Resync()
        {
            forger = null;
            Held.Clear();
        }

        public bool Drains { get; } = drains;

        public List<byte[]> Held { get; } = [];

        public List<byte[]> Corpus { get; } = [];
    }
}

internal sealed class HostileRoomException(string message, Exception? inner) : Exception(message, inner);

/// <summary>What the hostile sessions achieved over a run, for the report of a long run.</summary>
internal sealed class HostileStats
{
    public Dictionary<SessionCloseReason, int> CloseReasons { get; } = [];

    public long MaxPendingExcess { get; private set; }

    public int MaxAwarenessIds { get; private set; }

    /// <summary>Per kind of message: how many were sent, closed their session, and closed it but changed the document.</summary>
    public Dictionary<string, (int Sent, int Closed, int ClosedButChanged)> Kinds { get; } = [];

    public void Sent(string kind, bool closed, bool changed)
    {
        kind = new string(kind.Where(c => !char.IsDigit(c)).ToArray());
        var (sent, closedCount, closedButChanged) = Kinds.GetValueOrDefault(kind);
        Kinds[kind] = (sent + 1, closedCount + (closed ? 1 : 0), closedButChanged + (closed && changed ? 1 : 0));
    }

    public void Closed(SessionCloseReason reason) => CloseReasons[reason] = CloseReasons.GetValueOrDefault(reason) + 1;

    public void PendingExcess(long bytes) => MaxPendingExcess = Math.Max(MaxPendingExcess, bytes);

    public void AwarenessIds(int count) => MaxAwarenessIds = Math.Max(MaxAwarenessIds, count);

    public void Add(HostileStats other)
    {
        foreach (var (reason, count) in other.CloseReasons)
        {
            CloseReasons[reason] = CloseReasons.GetValueOrDefault(reason) + count;
        }

        foreach (var (kind, (sent, closed, changed)) in other.Kinds)
        {
            var (s, c, ch) = Kinds.GetValueOrDefault(kind);
            Kinds[kind] = (s + sent, c + closed, ch + changed);
        }

        PendingExcess(other.MaxPendingExcess);
        AwarenessIds(other.MaxAwarenessIds);
    }

    public override string ToString() =>
        $"closed {string.Join(", ", CloseReasons.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}"))}; most bytes pending {MaxPendingExcess}; most awareness states {MaxAwarenessIds}\n"
        + string.Join('\n', Kinds.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"  {e.Key}: sent {e.Value.Sent}, closed {e.Value.Closed}, of them after a change {e.Value.ClosedButChanged}"));
}
