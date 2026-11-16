using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists (starts and run-head index), unless
/// YCS_NO_RECOUNT=1 (to see what a property alone finds of a sabotaged index); with leaves of 4 positions
/// (YCS_LEAF_CAPACITY sets another); YCS_SLAB_FIRST sets the first size of the slab's page 0 (1: every allocation of it copies).
/// </summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        CheckAfterTransactions(null) = Environment.GetEnvironmentVariable("YCS_NO_RECOUNT") != "1";

        // Leaves of 4 positions: the short lists of the scripts split and merge leaves.
        LeafCapacity(null) = Environment.GetEnvironmentVariable("YCS_LEAF_CAPACITY") is { Length: > 0 } c ? int.Parse(c, System.Globalization.CultureInfo.InvariantCulture) : 4;

        if (Environment.GetEnvironmentVariable("YCS_SLAB_FIRST") is { Length: > 0 } first)
        {
            SlabFirst(null) = int.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The first size of page 0 of the slabs made from now on (0: the default).</summary>
    internal static int FirstPageSlots
    {
        get => SlabFirst(null);
        set => SlabFirst(null) = value;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "FirstPageSlotsForTests")]
    private static extern ref int SlabFirst([UnsafeAccessorType("Ycs.Slab, Ycs")] object slab);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "LeafCapacityForTests")]
    private static extern ref int LeafCapacity([UnsafeAccessorType("Ycs.ClientStructs, Ycs")] object structs);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CheckAfterTransactionsForTests")]
    private static extern ref bool CheckAfterTransactions([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);
}
