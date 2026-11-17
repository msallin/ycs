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
        StructStore.IntegrationPlan.CompareRestForTests = true;
        YEvent.CheckViewsForTests = true;

        // Leaves of 2 positions, so that short lists split and merge leaves.
        ClientStructs.LeafCapacityForTests = 2;

        if (Environment.GetEnvironmentVariable("YCS_SLAB_FIRST") is { Length: > 0 } first)
        {
            Slab.FirstPageSlotsForTests = int.Parse(first, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Pages of n characters at most, the first n/4 (at least 1), so that runs outgrow pages and cross them.
        if (Environment.GetEnvironmentVariable("YCS_ARENA_PAGE") is { Length: > 0 } page)
        {
            Arena<char>.MaxPageForTests = int.Parse(page, System.Globalization.CultureInfo.InvariantCulture);
            Arena<char>.FirstPageForTests = Math.Max(1, Arena<char>.MaxPageForTests / 4);
        }

        ItemRef.CopyOnMergeForTests = Environment.GetEnvironmentVariable("YCS_ARENA_COPY") == "1";
    }
}
