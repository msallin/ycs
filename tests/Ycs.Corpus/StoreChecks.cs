using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Every transaction of this suite ends with a recount of the struct lists (starts and run-head index, in blocks of 2 positions).</summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        StructStore.CheckAfterTransactionsForTests = true;

        // Blocks of 2 positions in the run-head index, so that short lists cross block boundaries.
        ClientStructs.BlockShiftForNewLists = 1;
    }
}
