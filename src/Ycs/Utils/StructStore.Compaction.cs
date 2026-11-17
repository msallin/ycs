namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>When the tests force a compaction of the character arena (<see cref="Slab.CompactChars"/>).</summary>
    internal enum CompactionMode
    {
        /// <summary>Only when <see cref="CompactIfWorthwhile"/> says so, at the end of a batch.</summary>
        None,

        /// <summary>After every transaction's cleanup and at the end of every batch; it replaces the production trigger.</summary>
        Always,

        /// <summary><see cref="Always"/>, and in the middle of a batch: at each struct added, each item made and each append to a run, and between the garbage collection and the merging of a transaction.</summary>
        MidBatch,
    }

    internal static CompactionMode CompactionForTests;

    /// <summary>
    /// Compacts the character arena when its garbage outweighs the live characters and the garbage made since the last
    /// compaction amounts to a largest page and to the number of slots, which a compaction walks
    /// (<see cref="Arena{T}.WorthCompacting"/>). Called where no batch is in progress: nothing then holds an offset.
    /// </summary>
    internal void CompactIfWorthwhile()
    {
        if (CompactionForTests != CompactionMode.None)
        {
            Slab.CompactCharsForTests();
        }
        else if (Slab.CharsOrNull is { } chars && chars.WorthCompacting(Slab.LiveCount))
        {
            Slab.CompactChars();
        }
    }

    /// <summary>
    /// Compacts where the test mode asks for it: in the middle of a batch (<paramref name="midBatch"/>, the caller holds
    /// no offset and its slots' lengths equal their ranges') for <see cref="CompactionMode.MidBatch"/>, after a
    /// transaction's cleanup for every mode but <see cref="CompactionMode.None"/>.
    /// </summary>
    internal static void CompactForTests(Slab slab, bool midBatch)
    {
        if (midBatch ? CompactionForTests == CompactionMode.MidBatch : CompactionForTests != CompactionMode.None)
        {
            slab.CompactCharsForTests();
        }
    }
}
