using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists (starts, leaves and run-head index) and a
/// comparison of every struct with its slot, and every event compares its views; the other switches come from the
/// environment (<see cref="TestHooks.ApplyEnvironment"/>, which lists the variables).
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
    }
}
