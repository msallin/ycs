using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Every transaction of this suite ends with a recount of the struct lists (starts, leaves and run-head index, in leaves of 2 positions).</summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        StructStore.CheckAfterTransactionsForTests = true;

        // Leaves of 2 positions, so that short lists split and merge leaves.
        ClientStructs.LeafCapacityForTests = 2;
    }
}
