namespace Ycs;

/// <summary>
/// The value ranges and nesting limits every decoder enforces, so that nothing derived from
/// untrusted input overflows, recurses without bound or allocates more than the input justifies.
/// </summary>
internal static class Limits
{
    /// <summary>
    /// Clocks and struct lengths: every struct, delete range and state-vector entry ends at or before
    /// this clock, so a clock, a length and any gap between two clocks fits an <see cref="int"/>.
    /// About two billion operations per client id; Yjs gives no other bound in practice.
    /// </summary>
    public const long MaxClock = int.MaxValue;

    /// <summary>Client ids: Yjs' are random 32-bit unsigned integers.</summary>
    public const long MaxClient = uint.MaxValue;

    /// <summary>
    /// Nesting depth of 'any' and JSON values, and of shared types inside each other, so that the
    /// recursive code that walks them (encoding, deleting, garbage collection, ToJson) cannot
    /// overflow the stack.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// The default of <see cref="YDocOptions.MaxStructsPerUpdate"/> and
    /// <see cref="UpdateOperations.MaxStructsPerUpdate"/>. The v2 format run-length encodes its
    /// columns, so a few bytes can describe any number of structs (Yjs writes 20,000 deleted items
    /// inserted at the front of a list in 42 bytes); no ratio to the input size bounds valid updates.
    /// </summary>
    public const long DefaultMaxStructsPerUpdate = 1 << 21;

    /// <summary>
    /// The structs a v2 update of <paramref name="remaining"/> bytes may hold: <paramref name="limit"/>,
    /// but never fewer than one per byte, which a v1 update needs at least anyway.
    /// </summary>
    public static long MaxStructsV2(long limit, int remaining) => Math.Max(limit, remaining);

    public static long CheckMaxStructs(long value) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The limit must be positive.");

    /// <summary>A capacity for a collection of <paramref name="count"/> decoded elements, each taking at least one of the <paramref name="remaining"/> input bytes.</summary>
    public static int Capacity(long count, int remaining) => (int)Math.Clamp(Math.Min(count, remaining), 0, int.MaxValue);

    public static long CheckClock(long clock)
    {
        if ((ulong)clock > MaxClock)
        {
            throw new YUpdateException($"A clock of {clock} is out of range (0 to {MaxClock}).");
        }

        return clock;
    }

    public static long CheckClient(long client)
    {
        if ((ulong)client > MaxClient)
        {
            throw new YUpdateException($"A client id of {client} is out of range (0 to {MaxClient}).");
        }

        return client;
    }

    /// <summary>Checks that [<paramref name="clock"/>, <paramref name="clock"/> + <paramref name="length"/>) is non-empty and within the clock range.</summary>
    public static void CheckRange(long clock, long length)
    {
        if (length <= 0 || (ulong)clock > MaxClock || length > MaxClock - clock)
        {
            throw new YUpdateException($"A range of length {length} at clock {clock} is empty or ends beyond clock {MaxClock}.");
        }
    }

    public static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new YUpdateException($"Values or shared types are nested deeper than {MaxDepth} levels.");
        }
    }
}
