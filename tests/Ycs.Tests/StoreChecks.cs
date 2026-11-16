using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Every transaction of this suite ends with a recount of the struct lists (starts, leaves and run-head index) and a
/// comparison of every struct with its slot. YCS_LEAF_CAPACITY sets the leaf capacity of the lists (default: the
/// library's); YCS_SLAB_FIRST the first size of the slab's page 0 (1: every allocation of it copies); YCS_POISON=1
/// replaces a collected type's handle by an unrelated live item's (a reused slot), so that any read of the struct shows.
/// </summary>
[TestClass]
public static class StoreChecks
{
    [AssemblyInitialize]
    public static void RecountAfterEveryTransaction(TestContext context)
    {
        StructStore.CheckAfterTransactionsForTests = true;
        YEvent.CheckViewsForTests = true;
        UpdateReader.StageForTests = true;
        YType.PoisonCollectedHandleForTests = Environment.GetEnvironmentVariable("YCS_POISON") == "1";
        if (Environment.GetEnvironmentVariable("YCS_LEAF_CAPACITY") is { Length: > 0 } capacity)
        {
            ClientStructs.LeafCapacityForTests = int.Parse(capacity, CultureInfo.InvariantCulture);
        }

        if (Environment.GetEnvironmentVariable("YCS_SLAB_FIRST") is { Length: > 0 } first)
        {
            Slab.FirstPageSlotsForTests = int.Parse(first, CultureInfo.InvariantCulture);
        }
    }
}
