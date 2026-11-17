using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists, with leaves of 2 positions so that short
/// lists split and merge leaves; the other switches come from the environment (<see cref="TestHooks.ApplyEnvironment"/>).
/// </summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        TestHooks.ApplyEnvironment();
        TestHooks.CheckAfterTransactions = true;
        TestHooks.CheckViews = true;
        TestHooks.LeafCapacity = 2;
    }
}
