namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>When the tests force a compaction of the character arena (<see cref="Slab.CompactChars"/>).</summary>
    internal enum CompactionMode
    {
        /// <summary>Only when <see cref="CompactIfWorthwhile"/> says so, at the end of a batch.</summary>
        None,

        /// <summary>After every transaction's cleanup and at the end of every batch.</summary>
        Always,

        /// <summary><see cref="Always"/>, and in the middle of a batch: at each struct added, each item made and each append to a run, and between the garbage collection and the merging of a transaction.</summary>
        MidBatch,
    }

    internal static CompactionMode CompactionForTests;

    /// <summary>
    /// Compacts the character arena when its garbage outweighs the live characters and amounts to a largest page
    /// (<see cref="Arena{T}.WorthCompacting"/>). Called where no batch is in progress: nothing then holds an offset.
    /// </summary>
    internal void CompactIfWorthwhile()
    {
        if (CompactionForTests != CompactionMode.None)
        {
            Slab.CompactCharsForTests();
        }
        else if (Slab.CharsOrNull is { WorthCompacting: true })
        {
            Slab.CompactChars();
        }
    }

    /// <summary>Compacts in the middle of a batch when the tests ask for it; the caller holds no offset.</summary>
    internal static void CompactMidBatchForTests(Slab slab)
    {
        if (CompactionForTests == CompactionMode.MidBatch)
        {
            slab.CompactCharsForTests();
        }
    }
}
