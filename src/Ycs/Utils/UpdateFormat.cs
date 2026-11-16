#nullable enable

namespace Ycs;

/// <summary>The binary encoding of an update (and of a snapshot): the same information in two layouts.</summary>
public enum UpdateFormat
{
    /// <summary>The original encoding, which every y-protocols provider sends (Yjs' <c>encodeStateAsUpdate</c>).</summary>
    V1 = 0,

    /// <summary>The denser encoding (Yjs' <c>encodeStateAsUpdateV2</c>).</summary>
    V2 = 1,
}

internal static class UpdateFormatExtensions
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is neither defined value.</exception>
    public static bool IsV2(this UpdateFormat format) => format switch
    {
        UpdateFormat.V1 => false,
        UpdateFormat.V2 => true,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an UpdateFormat."),
    };
}
