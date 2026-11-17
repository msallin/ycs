namespace Ycs;

/// <summary>
/// Every process-wide switch the tests flip. Each is a static field read inline where it acts (one load and one
/// branch), set once by a suite's <c>StoreChecks</c> (<see cref="ApplyEnvironment"/> reads the environment variables
/// named here) or for the length of one test. Nothing in <c>src</c> sets one.
/// The per-document switches are <c>YDoc.*ForTests</c> and <c>StructStore.PairwiseMergeForTests</c>; the recount
/// they switch on is <c>StructStore.CheckForTests</c> (this folder).
/// </summary>
internal static class TestHooks
{
    /// <summary>When the tests force a compaction of the arenas (<see cref="Slab.CompactChars"/>, <see cref="Slab.CompactValues"/>).</summary>
    internal enum CompactionMode
    {
        /// <summary>Only when <see cref="StructStore.CompactIfWorthwhile"/> says so, at the end of a batch.</summary>
        None,

        /// <summary>After every transaction's cleanup and at the end of every batch; it replaces the production trigger.</summary>
        Always,

        /// <summary><see cref="Always"/>, and in the middle of a batch: at each struct added, each item made and each append to a run, and between the garbage collection and the merging of a transaction.</summary>
        MidBatch,
    }

    /// <summary>YCS_COMPACT=always|midbatch: compacts the arenas as <see cref="CompactionMode"/> says.</summary>
    internal static CompactionMode Compaction;

    /// <summary>YCS_COMPACT_EVERY=1: <see cref="Slab.CompactForTests"/> compacts every time, whatever the size of the slab.</summary>
    internal static bool CompactEveryTime;

    /// <summary>YCS_SLAB_FIRST=n: the first size of page 0 for slabs made without one (1 makes every allocation of page 0 copy); 0: the default.</summary>
    internal static int SlabFirstPageSlots;

    /// <summary>
    /// YCS_LEAF_CAPACITY=n: when not 0, the leaf capacity of the lists made from now on instead of
    /// <see cref="ClientStructs.LeafCapacity"/> (at least 2), so that short lists split and merge leaves.
    /// </summary>
    internal static int LeafCapacity;

    /// <summary>YCS_ARENA_COPY=1: a merge of two Strings or Any runs copies even when the right range follows the left.</summary>
    internal static bool CopyOnMerge;

    /// <summary>YCS_POISON=1: after collection a type's handle names an unrelated live item, as a reused slot would, so any read of the struct instead of the facts changes behaviour.</summary>
    internal static bool PoisonCollectedHandle;

    /// <summary>Every transaction ends with <see cref="StructStore.CheckForTests"/> (no variable; a suite's <c>StoreChecks</c> sets it).</summary>
    internal static bool CheckAfterTransactions;

    /// <summary>
    /// Every event, when it is made (the observers are called right after), compares the views of the structs of its
    /// target's lists, and its <see cref="YEvent.Changes"/>, when collected, those of the added and deleted structs, with the
    /// structs (<see cref="ItemView.CheckEvent"/>); no variable.
    /// </summary>
    internal static bool CheckViews;

    /// <summary>YCS_ARENA_PAGE=n: arenas made while <see cref="Max"/> is not 0 have pages of at most n elements, the first n/4 (at least 1); 0: the arena's own sizes.</summary>
    internal static class ArenaPages<T>
    {
        /// <summary>The first page size of arenas made while it is not 0.</summary>
        internal static int First;

        /// <summary>The largest page size of arenas made while it is not 0.</summary>
        internal static int Max;
    }

    /// <summary>
    /// Compacts where the test mode asks for it: in the middle of a batch (<paramref name="midBatch"/>, the caller holds
    /// no offset and its slots' lengths equal their ranges') for <see cref="CompactionMode.MidBatch"/>, after a
    /// transaction's cleanup for every mode but <see cref="CompactionMode.None"/>.
    /// </summary>
    internal static void Compact(Slab slab, bool midBatch)
    {
        if (midBatch ? Compaction == CompactionMode.MidBatch : Compaction != CompactionMode.None)
        {
            slab.CompactForTests();
        }
    }

    /// <summary>Sets the switches that have a variable from the environment; those unset are switched off (0, false, none).</summary>
    internal static void ApplyEnvironment()
    {
        PoisonCollectedHandle = Environment.GetEnvironmentVariable("YCS_POISON") == "1";
        CopyOnMerge = Environment.GetEnvironmentVariable("YCS_ARENA_COPY") == "1";
        CompactEveryTime = Environment.GetEnvironmentVariable("YCS_COMPACT_EVERY") == "1";
        Compaction = Environment.GetEnvironmentVariable("YCS_COMPACT") switch
        {
            "always" => CompactionMode.Always,
            "midbatch" => CompactionMode.MidBatch,
            _ => CompactionMode.None,
        };
        LeafCapacity = Number("YCS_LEAF_CAPACITY");
        SlabFirstPageSlots = Number("YCS_SLAB_FIRST");

        var page = Number("YCS_ARENA_PAGE");
        ArenaPages<char>.Max = ArenaPages<object?>.Max = page;
        ArenaPages<char>.First = ArenaPages<object?>.First = page == 0 ? 0 : Math.Max(1, page / 4);
    }

    private static int Number(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } text ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
}
