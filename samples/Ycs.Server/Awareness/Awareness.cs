using System.Text;
using System.Text.Json;

namespace Ycs.Server.Awareness;

/// <summary>
/// The remote states of y-protocols/awareness (clocks, null states, the 30 s timeout), byte-compatible with
/// <c>applyAwarenessUpdate</c>, <c>encodeAwarenessUpdate</c> and <c>removeAwarenessStates</c>. The server has no
/// state of its own, so none of the local-state rules exist here. Not thread-safe: the room calls every member
/// under its lock.
/// </summary>
internal sealed class Awareness(TimeProvider time)
{
    /// <summary>Milliseconds without a renewal after which a state is removed.</summary>
    public const long OutdatedTimeoutMs = 30_000;

    // lib0 reads varuints into JS numbers: ids and clocks are exact up to 2^53 - 1, and so are they here.
    internal const ulong MaxSafeInteger = (1UL << 53) - 1;

    // A state is the JSON text as it arrived, so that encoding it again is byte-identical.
    private readonly Dictionary<ulong, string> _states = [];

    // Meta outlives the state: a removed client keeps its clock, an older message cannot revive it.
    private readonly Dictionary<ulong, Meta> _meta = [];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly record struct Meta(ulong Clock, long LastUpdated);

    /// <summary>One client of an awareness message: its state as JSON text, null for a removal.</summary>
    internal readonly record struct Entry(ulong ClientId, ulong Clock, string? State);

    public IReadOnlyCollection<ulong> ClientIds => _states.Keys;

    public bool TryGetState(ulong clientId, out string state) => _states.TryGetValue(clientId, out state!);

    public bool TryGetClock(ulong clientId, out ulong clock)
    {
        var found = _meta.TryGetValue(clientId, out var meta);
        clock = meta.Clock;
        return found;
    }

    /// <summary>
    /// Applies an awareness message with y-protocols' clock rules. A malformed message throws
    /// <see cref="YUpdateException"/> and changes nothing.
    /// </summary>
    public AwarenessChange ApplyUpdate(ReadOnlySpan<byte> update) => Apply(Decode(update));

    /// <summary>Applies decoded entries with y-protocols' clock rules; <see cref="Decode"/> needs no instance.</summary>
    public AwarenessChange Apply(IReadOnlyList<Entry> entries)
    {
        var now = Now();
        List<ulong> added = [], updated = [], changed = [], removed = [];

        foreach (var (clientId, clock, state) in entries)
        {
            var hasMeta = _meta.TryGetValue(clientId, out var meta);
            var currClock = hasMeta ? meta.Clock : 0;
            if (!(currClock < clock || (currClock == clock && state is null && _states.ContainsKey(clientId))))
            {
                continue;
            }

            var hadState = _states.TryGetValue(clientId, out var prev);
            if (state is null)
            {
                _states.Remove(clientId);
            }
            else
            {
                _states[clientId] = state;
            }

            _meta[clientId] = new Meta(clock, now);

            if (!hasMeta && state is not null)
            {
                added.Add(clientId);
            }
            else if (hasMeta && state is null)
            {
                removed.Add(clientId);
            }
            else if (state is not null)
            {
                if (!hadState || (state != prev && !JsonDeepEquals(state, prev!)))
                {
                    changed.Add(clientId);
                }

                updated.Add(clientId);
            }
        }

        return new AwarenessChange(added, updated, changed, removed);
    }

    /// <summary>
    /// Encodes the given clients, in the given order: their state, or null for a client that has none.
    /// Throws <see cref="ArgumentException"/> for a client this instance never saw.
    /// </summary>
    public byte[] EncodeUpdate(IReadOnlyCollection<ulong> clientIds)
    {
        using var buffer = new MemoryStream();
        WriteVarUint(buffer, (ulong)clientIds.Count);
        foreach (var clientId in clientIds)
        {
            if (!_meta.TryGetValue(clientId, out var meta))
            {
                throw new ArgumentException($"Client {clientId} has no awareness state or clock.", nameof(clientIds));
            }

            WriteVarUint(buffer, clientId);
            WriteVarUint(buffer, meta.Clock);
            var json = _states.TryGetValue(clientId, out var state) ? state : "null";
            var bytes = StrictUtf8.GetBytes(json);
            WriteVarUint(buffer, (ulong)bytes.Length);
            buffer.Write(bytes);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Removes the states of the given clients that have one. As in y-protocols, a remote client's clock stays,
    /// so the null state an encoding then carries is accepted by every peer that holds the old state.
    /// </summary>
    public AwarenessChange RemoveStates(IEnumerable<ulong> clientIds)
    {
        List<ulong> removed = [];
        foreach (var clientId in clientIds)
        {
            if (_states.Remove(clientId))
            {
                removed.Add(clientId);
            }
        }

        return removed.Count == 0 ? AwarenessChange.None : new AwarenessChange([], [], [], removed);
    }

    /// <summary>
    /// Removes the states not renewed for <see cref="OutdatedTimeoutMs"/>; the room checks about every 3 s. Unlike
    /// y-protocols, it also forgets the clock of a client that has had no state for as long: a message older than
    /// that clock is no longer rejected then, but no client sends one after its own newer ones, and the clocks of
    /// every client ever seen would otherwise stay for the life of the room. A state removed in this call keeps its
    /// clock until the next, for the removal the caller broadcasts.
    /// </summary>
    public AwarenessChange RemoveOutdated()
    {
        var now = Now();
        List<ulong> forgotten = [];
        foreach (var (clientId, meta) in _meta)
        {
            if (OutdatedTimeoutMs <= now - meta.LastUpdated && !_states.ContainsKey(clientId))
            {
                forgotten.Add(clientId);
            }
        }

        foreach (var clientId in forgotten)
        {
            _meta.Remove(clientId);
        }

        List<ulong> outdated = [];
        foreach (var (clientId, meta) in _meta)
        {
            if (OutdatedTimeoutMs <= now - meta.LastUpdated && _states.ContainsKey(clientId))
            {
                outdated.Add(clientId);
            }
        }

        return RemoveStates(outdated);
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// Decodes an awareness message; one that is malformed or names more than <paramref name="maxEntries"/> clients
    /// throws <see cref="YUpdateException"/>, before any of its JSON is parsed.
    /// </summary>
    public static List<Entry> Decode(ReadOnlySpan<byte> data, int maxEntries = int.MaxValue)
    {
        try
        {
            var pos = 0;
            var count = ReadVarUint(data, ref pos);
            if (count > (ulong)maxEntries)
            {
                throw new YUpdateException($"The awareness message names more than {maxEntries} clients.");
            }

            // An entry takes at least 4 bytes (id, clock, length, and the shortest JSON text).
            var entries = new List<Entry>((int)Math.Min(count, (ulong)(data.Length / 4)));
            for (ulong i = 0; i < count; i++)
            {
                var clientId = ReadVarUint(data, ref pos);
                var clock = ReadVarUint(data, ref pos);
                var length = ReadVarUint(data, ref pos);
                if (length > (ulong)(data.Length - pos))
                {
                    throw new YUpdateException("The awareness message ends inside a state.");
                }

                var json = StrictUtf8.GetString(data.Slice(pos, (int)length));
                pos += (int)length;
                entries.Add(new Entry(clientId, clock, ParseState(json)));
            }

            return entries;
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or ArgumentException or OverflowException)
        {
            throw new YUpdateException("The awareness message is malformed.", e);
        }
    }

    // Null for the JSON text "null", the text itself for any other valid JSON.
    private static string? ParseState(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Null ? null : json;
    }

    private static ulong ReadVarUint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; shift < 56; shift += 7)
        {
            if (pos >= data.Length)
            {
                throw new YUpdateException("The awareness message ends inside a number.");
            }

            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value <= MaxSafeInteger ? value : throw new YUpdateException("A number of the awareness message exceeds 2^53 - 1.");
            }
        }

        throw new YUpdateException("A number of the awareness message exceeds 2^53 - 1.");
    }

    private static void WriteVarUint(MemoryStream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    // lib0's equalityDeep on parsed values: object keys unordered (the last of a duplicate key wins), numbers as doubles.
    private static bool JsonDeepEquals(string a, string b)
    {
        using var da = JsonDocument.Parse(a);
        using var db = JsonDocument.Parse(b);
        return Equal(da.RootElement, db.RootElement);
    }

    private static bool Equal(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var pa = LastWins(a);
                var pb = LastWins(b);
                if (pa.Count != pb.Count)
                {
                    return false;
                }

                foreach (var (name, value) in pa)
                {
                    if (!pb.TryGetValue(name, out var other) || !Equal(value, other))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength())
                {
                    return false;
                }

                using (var ea = a.EnumerateArray().GetEnumerator())
                using (var eb = b.EnumerateArray().GetEnumerator())
                {
                    while (ea.MoveNext() && eb.MoveNext())
                    {
                        if (!Equal(ea.Current, eb.Current))
                        {
                            return false;
                        }
                    }
                }

                return true;
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Number:
                return ToDouble(a) == ToDouble(b);
            default:
                return true;
        }
    }

    private static double ToDouble(JsonElement number) => double.Parse(number.GetRawText(), System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, JsonElement> LastWins(JsonElement obj)
    {
        Dictionary<string, JsonElement> map = [];
        foreach (var property in obj.EnumerateObject())
        {
            map[property.Name] = property.Value;
        }

        return map;
    }
}
