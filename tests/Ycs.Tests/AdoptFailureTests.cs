using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>An update that makes Integrate throw after the plan split a GC struct leaves the store consistent, and its freed slot reusable.</summary>
[TestClass]
public class AdoptFailureTests
{
    // Document a: array "a" = [ map { k: "xy" } ]; the map is removed and collected, so (1,1) len 2 is a GC struct.
    // The hostile update comes from another document of the same client id: (1,0) len 1 known, (1,1) len 3 with
    // left origin (1,0), so the plan integrates it from offset 2 and Integrate meets a GC left with a parent.
    private static (YDoc A, byte[] Hostile) GcContinuation()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var arr = a.GetArray("a");
        var text = new YText();
        arr.Add(text);
        text.Insert(0, "xy");
        arr.RemoveAt(0);
        var gc = a.Store.Find(new ItemId(1, 1));
        Assert.IsTrue(gc.IsGC && gc.Length == 2, "setup: (1,1) is not a GC struct of length 2");

        var c = new YDoc(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };
        c.GetArray("a").Add(new YMap());
        c.GetArray("a").Add("x", "y", "z");
        Assert.AreEqual(2, c.Store.Clients[1].Count, "setup: the hostile document does not hold two items");
        return (a, c.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Adopt_ThrowsMidIntegrate_StoreIsConsistentAndNoSlotLivesWithoutAStruct()
    {
        var (a, hostile) = GcContinuation();
        var liveBefore = a.Store.Slab.LiveCount;
        var structsBefore = 0;
        foreach (var s in a.Store.Clients.Values)
        {
            structsBefore += s.Count;
        }

        var thrown = Assert.ThrowsExactly<YUpdateException>(() => a.ApplyUpdate(hostile));
        Assert.IsTrue(thrown.Message.Contains("garbage collected, but has a parent", StringComparison.Ordinal), thrown.Message);

        a.Store.CheckForTests();
        var structsAfter = 0;
        foreach (var s in a.Store.Clients.Values)
        {
            structsAfter += s.Count;
        }

        // The GC struct was split at (1,2) by GetItemCleanEnd and merged back at the cleanup; no slot lives without a struct.
        Assert.AreEqual((structsBefore, liveBefore), (structsAfter, a.Store.Slab.LiveCount));

        // The document stays usable: an insert, an encode, a peer that loads it.
        a.GetArray("a").Add(7);
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        Assert.AreEqual((1, 7), (b.GetArray("a").Count, b.GetArray("a")[0].GetInt32()));
        a.Store.CheckForTests();
        b.Store.CheckForTests();
    }

    [TestMethod]
    public void Adopt_ThrowsMidIntegrate_FreedSlotReusesCleanly()
    {
        var (a, hostile) = GcContinuation();
        Assert.ThrowsExactly<YUpdateException>(() => a.ApplyUpdate(hostile));

        // Churn so that every freed slot is reused; every transaction runs the store checks.
        var arr = a.GetArray("a");
        for (var i = 0; i < 64; i++)
        {
            arr.Add(i);
            arr.RemoveAt(0);
        }

        a.Store.CheckForTests();
        Assert.IsTrue(a.Store.Slab.Violations().Count == 0);
    }
}
