using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ycs;

/// <summary>What a staged struct is; the values are the wire's content reference numbers, GC and Skip included.</summary>
internal enum StagedKind : byte
{
    GC = 0,
    Deleted = 1,
    Json = 2,
    Binary = 3,
    String = 4,
    Embed = 5,
    Format = 6,
    Type = 7,
    Any = 8,
    Doc = 9,
    Skip = 10,
}

internal enum StagedParent : byte
{
    None = 0,
    Root = 1,
    Id = 2,
}

/// <summary>
/// One decoded struct, without a reference field. A client is an index into the table's client list, -1 for none;
/// <see cref="Payload"/> is the offset of a String's characters or an Any's values, the index of a cold object, or, for
/// a Type, the index of its name (-1 for none). <see cref="Info"/>: bit 0 countable, bits 1..2 the
/// <see cref="StagedParent"/>, bits 8..15 the <see cref="StagedKind"/>, bits 16..23 a Type's type reference.
/// <see cref="ParentClient"/> is the client index of a parent id, or the object index of a root name.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StagedStruct
{
    public long Clock;
    public long OriginClock;
    public long ROriginClock;
    public long ParentClock;
    public int Length;
    public int Client;
    public int OriginClient;
    public int ROriginClient;
    public int ParentClient;
    public int ParentSub;
    public int Info;
    public int Payload;

    public readonly StagedKind Kind => (StagedKind)((Info >> 8) & 0xFF);

    public readonly bool Countable => (Info & 1) != 0;

    public readonly StagedParent ParentKind => (StagedParent)((Info >> 1) & 3);

    public readonly int TypeRef => (Info >> 16) & 0xFF;
}

/// <summary>
/// The structs of one update (or of one <c>UpdateOperations</c> call), in the order they were decoded: each client's
/// structs are one contiguous range in clock order, started by <see cref="BeginClient"/>, and a struct starts where
/// the one before it ends. Characters and values of the structs sit in the table's own buffers, in append order.
/// </summary>
/// <remarks>
/// Not thread-safe; one owner decodes, then reads. A reader that outlives a <see cref="Reset"/> compares the
/// <see cref="Version"/> it saw. Indexes, spans and refs are valid until the next <c>Add*</c> or <see cref="Reset"/>.
/// </remarks>
internal sealed class StagingTable
{
    /// <summary>The bytes <see cref="Reset"/> keeps across all buffers.</summary>
    internal const int RetainedBytesCap = 256 * 1024;

    /// <summary>The size of a <see cref="StagedStruct"/>: four longs and eight ints.</summary>
    internal const int StructBytes = 64;

    private const int FirstCapacity = 16;
    private const int MaxRetainedClients = 1024;

    private StagedStruct[] _structs = [];
    private char[] _chars = [];
    private object?[] _values = [];
    private object?[] _refs = [];
    private long[] _clients = [];
    private int[] _rangeStart = [];
    private int[] _rangeCount = [];
    private Dictionary<long, int> _clientIndex = [];
    private int _count;
    private int _charsUsed;
    private int _valuesUsed;
    private int _refsUsed;
    private int _clientCount;
    private int _open = -1;
    private long _nextClock;
    private int _lastClientIndex = -1;

    /// <summary>Bumped by every <see cref="Reset"/>.</summary>
    public int Version { get; private set; }

    public int Count => _count;

    public int ClientCount => _clientCount;

    /// <summary>The bytes the buffers hold now, used or not.</summary>
    public long CapacityBytes =>
        (long)_structs.Length * StructBytes + _chars.Length * sizeof(char) + (_values.Length + _refs.Length) * (long)IntPtr.Size
        + _clients.Length * sizeof(long) + (_rangeStart.Length + _rangeCount.Length) * sizeof(int);

    /// <summary>9.2: opens the range of <paramref name="client"/>, whose first struct starts at <paramref name="clock"/>.</summary>
    public void BeginClient(long client, long clock)
    {
        var index = ClientIndex(client);
        if (_rangeCount[index] != 0)
        {
            throw new InvalidOperationException($"Client {client} already has a range in this table; a client's structs must be one block.");
        }

        _open = index;
        _rangeStart[index] = _count;
        _nextClock = clock;
    }

    /// <summary>
    /// 9.2: stages a struct at the end of the open client's range. A String reserves <paramref name="length"/> characters
    /// (<see cref="Chars"/>), an Any <paramref name="length"/> values (<see cref="Values"/>). Parent, content object and
    /// type are set after.
    /// </summary>
    public int Add(StagedKind kind, int length, ID? leftOrigin = null, ID? rightOrigin = null)
    {
        if (_open < 0)
        {
            throw new InvalidOperationException("Add needs an open client: call BeginClient first.");
        }

        Debug.Assert(length > 0);
        if (_count == _structs.Length)
        {
            Grow(ref _structs, FirstCapacity);
        }

        var countable = kind is not (StagedKind.GC or StagedKind.Skip or StagedKind.Deleted or StagedKind.Format);
        ref var s = ref _structs[_count];
        s = new StagedStruct
        {
            Clock = _nextClock,
            Length = length,
            Client = _open,
            OriginClient = -1,
            ROriginClient = -1,
            ParentClient = -1,
            ParentSub = -1,
            Payload = -1,
            Info = (int)kind << 8 | (countable ? 1 : 0),
        };

        if (leftOrigin is { } left)
        {
            s.OriginClient = ClientIndex(left.Client);
            s.OriginClock = left.Clock;
        }

        if (rightOrigin is { } right)
        {
            s.ROriginClient = ClientIndex(right.Client);
            s.ROriginClock = right.Clock;
        }

        if (kind == StagedKind.String)
        {
            Reserve(ref _chars, ref _charsUsed, length);
            _structs[_count].Payload = _charsUsed - length;
        }
        else if (kind == StagedKind.Any)
        {
            Reserve(ref _values, ref _valuesUsed, length);
            _structs[_count].Payload = _valuesUsed - length;
        }

        _nextClock += length;
        _rangeCount[_open]++;
        return _count++;
    }

    /// <summary>9.2: the parent of struct <paramref name="i"/> as a root type's name.</summary>
    public void SetParentRoot(int i, string name)
    {
        ref var s = ref _structs[i];
        s.Info = s.Info & ~(3 << 1) | (int)StagedParent.Root << 1;
        s.ParentClient = AddRef(name);
    }

    /// <summary>9.2: the parent of struct <paramref name="i"/> as the id of the item that holds its type.</summary>
    public void SetParentId(int i, ID parent)
    {
        var client = ClientIndex(parent.Client);
        ref var s = ref _structs[i];
        s.Info = s.Info & ~(3 << 1) | (int)StagedParent.Id << 1;
        s.ParentClient = client;
        s.ParentClock = parent.Clock;
    }

    public void SetParentSub(int i, string parentSub) => _structs[i].ParentSub = AddRef(parentSub);

    /// <summary>9.2: the content object of a Format, Embed, Binary, Json or Doc struct.</summary>
    public void SetCold(int i, object content) => _structs[i].Payload = AddRef(content);

    /// <summary>9.2: a Type struct's type reference and, for an XML element or hook, its node name.</summary>
    public void SetType(int i, int typeRef, string? name)
    {
        ref var s = ref _structs[i];
        s.Info = s.Info & ~(0xFF << 16) | typeRef << 16;
        s.Payload = name == null ? -1 : AddRef(name);
    }

    /// <summary>9.3, 9.4, 9.5: struct <paramref name="i"/>; valid until the next <c>Add*</c>.</summary>
    public ref readonly StagedStruct At(int i)
    {
        Debug.Assert(!Outside(i, _count));
        return ref _structs[i];
    }

    public ID IdOf(int i) => new(_clients[At(i).Client], At(i).Clock);

    /// <summary>9.3, 9.5.</summary>
    public ID? LeftOrigin(int i) => At(i).OriginClient < 0 ? null : new ID(_clients[At(i).OriginClient], At(i).OriginClock);

    /// <summary>9.3, 9.5.</summary>
    public ID? RightOrigin(int i) => At(i).ROriginClient < 0 ? null : new ID(_clients[At(i).ROriginClient], At(i).ROriginClock);

    /// <summary>9.3, 9.5: the root name when <see cref="StagedStruct.ParentKind"/> is Root.</summary>
    public string ParentName(int i) => (string)_refs[At(i).ParentClient]!;

    /// <summary>9.3, 9.5: the parent item's id when <see cref="StagedStruct.ParentKind"/> is Id.</summary>
    public ID ParentId(int i) => new(_clients[At(i).ParentClient], At(i).ParentClock);

    public string? ParentSub(int i) => At(i).ParentSub < 0 ? null : (string)_refs[At(i).ParentSub]!;

    /// <summary>9.2 fills it, 9.3 and 9.5 read it: the characters of a String struct (its length), in UTF-16 units.</summary>
    public Span<char> Chars(int i)
    {
        Debug.Assert(At(i).Kind == StagedKind.String);
        return _chars.AsSpan(At(i).Payload, At(i).Length);
    }

    /// <summary>9.2 fills it, 9.3 and 9.5 read it: the values of an Any struct (its length).</summary>
    public Span<object?> Values(int i)
    {
        Debug.Assert(At(i).Kind == StagedKind.Any);
        return _values.AsSpan(At(i).Payload, At(i).Length);
    }

    /// <summary>9.3, 9.5: the object of a Format, Embed, Binary, Json or Doc struct.</summary>
    public object Cold(int i) => _refs[At(i).Payload]!;

    /// <summary>9.3, 9.5: a Type struct's node name, null for none.</summary>
    public string? TypeName(int i) => At(i).Payload < 0 ? null : (string)_refs[At(i).Payload]!;

    /// <summary>9.3, 9.4: the index of a client in this table, -1 when the table never saw it.</summary>
    public int FindClient(long client) => _clientIndex.TryGetValue(client, out var index) ? index : -1;

    /// <summary>9.3, 9.4: the client id of a client index.</summary>
    public long ClientAt(int clientIndex) => _clients[clientIndex];

    /// <summary>9.3, 9.4: the structs of a client, in clock order; an empty range for a client only named by an origin.</summary>
    public (int Start, int Count) RangeOf(int clientIndex) => (_rangeStart[clientIndex], _rangeCount[clientIndex]);

    /// <summary>
    /// Empties the table for the next use, keeping buffers as far as <see cref="RetainedBytesCap"/> allows (each buffer
    /// in the order structs, characters, values, objects, clients), and bumps <see cref="Version"/>. Drops the references
    /// held.
    /// </summary>
    public void Reset()
    {
        Poison();
        Array.Clear(_values, 0, _valuesUsed);
        Array.Clear(_refs, 0, _refsUsed);
        _count = 0;
        _charsUsed = 0;
        _valuesUsed = 0;
        _refsUsed = 0;
        _open = -1;
        _lastClientIndex = -1;
        _nextClock = 0;
        if (_clientCount > MaxRetainedClients)
        {
            _clientIndex = [];
            _clients = [];
            _rangeStart = [];
            _rangeCount = [];
        }
        else
        {
            _clientIndex.Clear();
            Array.Clear(_rangeStart, 0, _clientCount);
            Array.Clear(_rangeCount, 0, _clientCount);
        }

        _clientCount = 0;
        var left = RetainedBytesCap;
        Keep(ref _structs, StructBytes, ref left);
        Keep(ref _chars, sizeof(char), ref left);
        Keep(ref _values, IntPtr.Size, ref left);
        Keep(ref _refs, IntPtr.Size, ref left);
        Keep(ref _clients, sizeof(long) + 2 * sizeof(int), ref left);
        if (_clients.Length == 0)
        {
            _rangeStart = [];
            _rangeCount = [];
        }

        Version++;
    }

    /// <summary>Every inconsistency found by recounting the table: none when sound.</summary>
    public List<string> Violations()
    {
        var found = new List<string>();
        CheckBuffers(found);
        if (found.Count > 0)
        {
            return found;
        }

        CheckClients(found);
        var expectedChars = 0;
        var expectedValues = 0;
        var refsReferenced = 0;
        for (var i = 0; i < _count; i++)
        {
            ref var s = ref _structs[i];
            CheckStruct(i, ref s, found);
            if (s.Kind == StagedKind.String && s.Length > 0)
            {
                if (s.Payload != expectedChars)
                {
                    found.Add($"struct {i}: characters at {s.Payload}, expected {expectedChars}");
                }

                expectedChars += s.Length;
            }
            else if (s.Kind == StagedKind.Any && s.Length > 0)
            {
                if (s.Payload != expectedValues)
                {
                    found.Add($"struct {i}: values at {s.Payload}, expected {expectedValues}");
                }

                expectedValues += s.Length;
            }

            refsReferenced += ReferencedObjects(ref s, i, found);
        }

        if (expectedChars != _charsUsed)
        {
            found.Add($"{_charsUsed} characters used, structs hold {expectedChars}");
        }

        if (expectedValues != _valuesUsed)
        {
            found.Add($"{_valuesUsed} values used, structs hold {expectedValues}");
        }

        if (refsReferenced != _refsUsed)
        {
            found.Add($"{_refsUsed} objects held, structs refer to {refsReferenced}");
        }

        return found;
    }

    private void CheckBuffers(List<string> found)
    {
        if (_count > _structs.Length || _charsUsed > _chars.Length || _valuesUsed > _values.Length || _refsUsed > _refs.Length
            || _clientCount > _clients.Length || _clientCount > _rangeStart.Length || _clientCount > _rangeCount.Length)
        {
            found.Add("a buffer holds fewer entries than its count");
        }

        if (_clientIndex.Count != _clientCount)
        {
            found.Add($"{_clientCount} clients listed, the index holds {_clientIndex.Count}");
        }

        for (var i = _valuesUsed; i < _values.Length; i++)
        {
            if (_values[i] != null)
            {
                found.Add($"value {i} beyond the used {_valuesUsed} is not cleared");
                break;
            }
        }

        for (var i = _refsUsed; i < _refs.Length; i++)
        {
            if (_refs[i] != null)
            {
                found.Add($"object {i} beyond the used {_refsUsed} is not cleared");
                break;
            }
        }
    }

    private void CheckClients(List<string> found)
    {
        var covered = 0;
        var ranges = new List<(int Start, int Count, int Client)>();
        for (var c = 0; c < _clientCount; c++)
        {
            if (!_clientIndex.TryGetValue(_clients[c], out var index) || index != c)
            {
                found.Add($"client {_clients[c]} is not indexed at {c}");
            }

            if (_rangeCount[c] > 0)
            {
                ranges.Add((_rangeStart[c], _rangeCount[c], c));
            }
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var (start, count, client) in ranges)
        {
            if (start != covered)
            {
                found.Add($"client index {client}: range starts at {start}, expected {covered}");
            }

            covered = start + count;
            for (var i = start; i < covered && i < _count; i++)
            {
                if (_structs[i].Client != client)
                {
                    found.Add($"struct {i} is in the range of client index {client} but names {_structs[i].Client}");
                    break;
                }

                if (i > start && _structs[i].Clock != _structs[i - 1].Clock + _structs[i - 1].Length)
                {
                    found.Add($"struct {i}: clock {_structs[i].Clock} does not follow the struct before it");
                    break;
                }
            }
        }

        if (covered != _count)
        {
            found.Add($"ranges cover {covered} of {_count} structs");
        }
    }

    private void CheckStruct(int i, ref StagedStruct s, List<string> found)
    {
        var kind = (int)s.Kind;
        if (kind > (int)StagedKind.Skip)
        {
            found.Add($"struct {i}: unknown kind {kind}");
            return;
        }

        if (s.Length <= 0 || s.Clock < 0)
        {
            found.Add($"struct {i}: length {s.Length}, clock {s.Clock}");
        }

        if (Outside(s.Client, _clientCount) || Outside(s.OriginClient, _clientCount) && s.OriginClient != -1
            || Outside(s.ROriginClient, _clientCount) && s.ROriginClient != -1)
        {
            found.Add($"struct {i}: a client index is outside the list of {_clientCount}");
        }

        var item = s.Kind is not (StagedKind.GC or StagedKind.Skip);
        var origins = s.OriginClient >= 0 || s.ROriginClient >= 0;
        if (!item && (origins || s.ParentKind != StagedParent.None || s.ParentSub != -1))
        {
            found.Add($"struct {i}: a {s.Kind} has origins or a parent");
        }
        else if (item && origins == (s.ParentKind != StagedParent.None))
        {
            found.Add($"struct {i}: an item has {(origins ? "origins and a parent" : "neither origins nor a parent")}");
        }

        if (s.ParentKind == StagedParent.Id && Outside(s.ParentClient, _clientCount)
            || s.ParentKind == StagedParent.Root && (Outside(s.ParentClient, _refsUsed) || _refs[s.ParentClient] is not string)
            || (int)s.ParentKind > (int)StagedParent.Id)
        {
            found.Add($"struct {i}: the parent is invalid");
        }

        if (s.ParentSub != -1 && (Outside(s.ParentSub, _refsUsed) || _refs[s.ParentSub] is not string))
        {
            found.Add($"struct {i}: the parentSub is invalid");
        }

        if (s.Kind == StagedKind.Type && s.TypeRef > 6)
        {
            found.Add($"struct {i}: unknown type reference {s.TypeRef}");
        }

        if (s.Countable != (s.Kind is not (StagedKind.GC or StagedKind.Skip or StagedKind.Deleted or StagedKind.Format)))
        {
            found.Add($"struct {i}: countable does not match the kind {s.Kind}");
        }
    }

    // The objects struct i refers to by Payload; each object is referred to once, so the count is checked against _refsUsed.
    private int ReferencedObjects(ref StagedStruct s, int i, List<string> found)
    {
        var refs = (s.ParentKind == StagedParent.Root ? 1 : 0) + (s.ParentSub >= 0 ? 1 : 0);
        var payloadIsRef = s.Kind is StagedKind.Json or StagedKind.Binary or StagedKind.Embed or StagedKind.Format or StagedKind.Doc
            || s.Kind == StagedKind.Type && s.Payload >= 0;
        if (payloadIsRef)
        {
            refs++;
            if (Outside(s.Payload, _refsUsed) || _refs[s.Payload] == null)
            {
                found.Add($"struct {i}: the object at {s.Payload} is missing");
            }
        }
        else if (s.Kind is not (StagedKind.String or StagedKind.Any or StagedKind.Type) && s.Payload != -1)
        {
            found.Add($"struct {i}: a {s.Kind} has a payload");
        }

        return refs;
    }

    private static bool Outside(int index, int count) => index < 0 || index >= count;

    private int ClientIndex(long client)
    {
        if (_lastClientIndex >= 0 && _clients[_lastClientIndex] == client)
        {
            return _lastClientIndex;
        }

        if (!_clientIndex.TryGetValue(client, out var index))
        {
            if (_clientCount == _clients.Length)
            {
                var size = Math.Max(_clientCount * 2, 4);
                Array.Resize(ref _clients, size);
                Array.Resize(ref _rangeStart, size);
                Array.Resize(ref _rangeCount, size);
            }

            index = _clientCount++;
            _clients[index] = client;
            _rangeStart[index] = 0;
            _rangeCount[index] = 0;
            _clientIndex[client] = index;
        }

        return _lastClientIndex = index;
    }

    private int AddRef(object value)
    {
        if (_refsUsed == _refs.Length)
        {
            Grow(ref _refs, FirstCapacity);
        }

        _refs[_refsUsed] = value;
        return _refsUsed++;
    }

    private static void Reserve<T>(ref T[] buffer, ref int used, int length)
    {
        if (length > buffer.Length - used)
        {
            var needed = (long)used + length;
            var size = Math.Max(needed, Math.Min((long)buffer.Length * 2, Array.MaxLength));
            if (size > Array.MaxLength)
            {
                throw new InvalidOperationException("The staging table's buffer cannot hold this update.");
            }

            Array.Resize(ref buffer, (int)size);
        }

        used += length;
    }

    private static void Grow<T>(ref T[] buffer, int first) => Array.Resize(ref buffer, Math.Max(buffer.Length * 2, first));

    private static void Keep<T>(ref T[] buffer, int elementBytes, ref int left)
    {
        var bytes = (long)buffer.Length * elementBytes;
        if (bytes > left)
        {
            buffer = [];
        }
        else
        {
            left -= (int)bytes;
        }
    }

    // A reader that kept a ref, a span or an index across Reset reads these.
    [Conditional("DEBUG")]
    private void Poison()
    {
        Array.Fill(_structs, new StagedStruct
        {
            Clock = -0x5A5A5A5A,
            OriginClock = -0x5A5A5A5A,
            ROriginClock = -0x5A5A5A5A,
            ParentClock = -0x5A5A5A5A,
            Length = -0x5A5A5A5A,
            Client = -0x5A5A5A5A,
            OriginClient = -0x5A5A5A5A,
            ROriginClient = -0x5A5A5A5A,
            ParentClient = -0x5A5A5A5A,
            ParentSub = -0x5A5A5A5A,
            Info = -0x5A5A5A5A,
            Payload = -0x5A5A5A5A,
        }, 0, _count);
        Array.Fill(_chars, '\uDEAD', 0, _charsUsed);
    }
}
