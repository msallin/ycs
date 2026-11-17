#pragma warning disable CA1720 // The names of the kinds, as in JsonValueKind.
namespace Ycs;

/// <summary>What a <see cref="YValue"/> holds.</summary>
public enum YValueKind
{
    /// <summary>null, and what <c>default(YValue)</c> is.</summary>
    Null = 0,

    Boolean,

    /// <summary>A JavaScript number: a double, which also holds every integer within ±2^53.</summary>
    Number,

    /// <summary>A JavaScript BigInt, 64 bits signed.</summary>
    BigInteger,

    String,

    /// <summary>A byte array (a <c>Uint8Array</c>).</summary>
    Bytes,

    Array,

    /// <summary>An object with string keys.</summary>
    Object,

    /// <summary>A shared type (<see cref="YType"/>).</summary>
    SharedType,

    /// <summary>A subdocument.</summary>
    Document,
}
