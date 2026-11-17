namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// Compacts each arena (characters, values) when its garbage outweighs the live characters and the garbage made since the last
    /// compaction amounts to a largest page and to the number of slots, which a compaction walks
    /// (<see cref="Arena{T}.WorthCompacting"/>). Called where no batch is in progress: nothing then holds an offset.
    /// </summary>
    internal void CompactIfWorthwhile()
    {
        if (TestHooks.Compaction != TestHooks.CompactionMode.None)
        {
            Slab.CompactForTests();
            return;
        }

        if (Slab.CharsOrNull is { } chars && chars.WorthCompacting(Slab.LiveCount))
        {
            Slab.CompactChars();
        }

        if (Slab.ValuesOrNull is { } values && values.WorthCompacting(Slab.LiveCount))
        {
            Slab.CompactValues();
        }
    }
}
