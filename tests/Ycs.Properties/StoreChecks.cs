using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists (starts and run-head index), unless
/// YCS_NO_RECOUNT=1 (to see what a property alone finds of a sabotaged index); with leaves of 4 positions
/// (YCS_LEAF_CAPACITY sets another); the other switches come from the environment (<see cref="TestHooks.ApplyEnvironment"/>).
/// </summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        TestHooks.ApplyEnvironment();
        TestHooks.CheckAfterTransactions = Environment.GetEnvironmentVariable("YCS_NO_RECOUNT") != "1";

        // Leaves of 4 positions: the short lists of the scripts split and merge leaves.
        if (TestHooks.LeafCapacity == 0)
        {
            TestHooks.LeafCapacity = 4;
        }
    }
}
