using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Every transaction of this suite ends with a recount of the struct lists (starts and run-head index).</summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        StructStore.CheckAfterTransactionsForTests = true;

        if (Environment.GetEnvironmentVariable("YCS_SLAB_FIRST") is { Length: > 0 } first)
        {
            Slab.FirstPageSlotsForTests = int.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
