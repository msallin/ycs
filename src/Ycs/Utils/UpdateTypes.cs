#nullable enable

namespace Ycs;

/// <summary>
/// One entry of a decoded delete set: <c>[Clock, Clock + Length)</c> is deleted for the owning client
/// (<see cref="DecodedUpdate.DeleteSet"/>).
/// </summary>
public readonly record struct DeletedRange(long Clock, long Length);

/// <summary>
/// The structured view of an update: its structs, in the order they occur in the update (GC and Skip included), and its
/// delete set (<see cref="YUpdate.Decode"/>).
/// </summary>
public sealed class DecodedUpdate
{
    private IReadOnlyList<DecodedStruct>? _decodedStructs;

    internal DecodedUpdate(StagingTable structs, IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> deleteSet)
    {
        Staged = structs;
        DeleteSet = deleteSet;
    }

    /// <summary>The structs in the order of the update, held by a table of their own.</summary>
    internal StagingTable Staged { get; }

    /// <summary>One <see cref="DecodedStruct"/> per struct, in the order of the update, Skip and GC included.</summary>
    public IReadOnlyList<DecodedStruct> Structs => _decodedStructs ??= Detach(Staged);

    /// <summary>The deleted clock ranges of every client.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> DeleteSet { get; }

    private static DecodedStruct[] Detach(StagingTable table)
    {
        var result = new DecodedStruct[table.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = StructStaging.Decoded(table, i);
        }

        return result;
    }
}

/// <summary>
/// The clock ranges an update covers: for every client that contributed at least one struct, the clock of its first
/// struct (<see cref="From"/>) and the clock right after its last (<see cref="To"/>) (<see cref="YUpdate.ParseMeta"/>).
/// </summary>
public sealed record UpdateMeta(IReadOnlyDictionary<long, long> From, IReadOnlyDictionary<long, long> To);
