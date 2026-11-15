using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists (starts and run-head index), unless
/// YCS_NO_RECOUNT=1 (to see what a property alone finds of a sabotaged index); with blocks of 2 positions.
/// </summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        CheckAfterTransactions(null) = Environment.GetEnvironmentVariable("YCS_NO_RECOUNT") != "1";

        // Blocks of 2 positions in the run-head index: the short lists of the scripts cross many block boundaries.
        BlockShift(null) = 1;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "BlockShiftForNewLists")]
    private static extern ref int BlockShift([UnsafeAccessorType("Ycs.ClientStructs, Ycs")] object structs);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CheckAfterTransactionsForTests")]
    private static extern ref bool CheckAfterTransactions([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);
}
