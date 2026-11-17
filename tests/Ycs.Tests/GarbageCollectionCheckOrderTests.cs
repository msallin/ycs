using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Updates whose cleanup makes the garbage collection throw InvalidOperationException (a deleted type in which a key
/// holds a live item that is not its value) although the check of <see cref="GarbageCollectionCheckTests"/> should have
/// rejected them with a YUpdateException. Yjs 13.6.32 throws "Unexpected case" from Item.gc on the first and the third.
/// </summary>
[TestClass]
public class GarbageCollectionCheckOrderTests
{
    // Items 11:2 (a map), 11:3 (three values, key "m" of 11:2) and 300:3 (origin 11:3, so it splits the value and deletes only
    // the right part), and a delete range over 11:2. The value is split by a planned item that replaces it.
    private const string ReplacedAndDeleted = "0403AC02000001C40B000B01026263880B03027D017B3FF8000000000000000D00050B000401017403E4B8ADC40B000B000161C7F8FAFFFF0700F8FAFFFF07020128000B02016D0375027D017701787E780701017803017004F8FAFFFF0700080101610375027D017701787D0175027D017701782101016D016C0241F8FAFFFF0703010A02010B010201";

    // The same, in two updates: the delete range waits (state of client 11 is 0) and is applied when the structs arrive.
    private const string WaitingDeletes = "03040A0188F0A2040302770173770173070101610428000B0E016B01780A03010B0F07000A03030364697602F0A2040907000B080303646976C40C080C0803E4B8AD010B030F0200030F02";
    private const string Structs = "0403AC02000001C40B000B01026263880B03027D017B3FF8000000000000000D00050B000401017403E4B8ADC40B000B000161C7F8FAFFFF0700F8FAFFFF07020128000B02016D0375027D017701787E780701017803017004F8FAFFFF0700080101610375027D017701787D0175027D017701782101016D016C0241F8FAFFFF0703010A0200";

    private const string Completing = "02040C0007010178062101016D016B0204000C000378C3A9870C0006020B0000052801016D016B027B3FF80000000000007B3FF8000000000000020B02050104010C010302";

    [TestMethod]
    public void ApplyUpdate_ValueSplitByItsReplacerInTheSameUpdateAndItsTypeDeleted_RejectsAndLeavesTheDocumentUnchanged()
    {
        var doc = new YDoc();
        var before = Convert.ToBase64String(doc.EncodeStateAsUpdate());

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromHexString(ReplacedAndDeleted)));

        Assert.AreEqual(before, Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ApplyUpdate_DeletedPlannedTypeTheFilterKeeps_IsNotCollectedAndApplies()
    {
        var asked = 0;
        var doc = new YDoc(new YDocOptions { GarbageCollectionFilter = _ => { asked++; return false; } });

        doc.ApplyUpdate(Convert.FromHexString(ReplacedAndDeleted));

        Assert.IsTrue(asked > 0 && doc.Store.PendingBytes == 0);
    }

    [TestMethod]
    public void ApplyUpdate_WaitingDeleteRangeAppliesToASplitValue_Rejects()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(Convert.FromHexString(WaitingDeletes));

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromHexString(Structs)));
    }

    [TestMethod]
    public void ApplyUpdate_WaitingRangesOfTwoClients_CheckSeesThemInTheOrderTheyAreApplied()
    {
        // The property engine's seed 1302 in reversed order: the update that completes the structs finds the ranges of
        // client 12 (deleting the type 12:6) and of client 2147483000 (splitting its value) waiting.
        var doc = WithWaitingRanges(new YDocOptions());

        // The check rejects the structs that waited (their plan, once the ranges are applied in order, splits a value), the update
        // stands and what waited is dropped (reported by PendingDropped): the document stays one another can be synced from.
        var dropped = 0;
        doc.PendingDropped += (_, _) => dropped++;
        doc.ApplyUpdate(Convert.FromHexString(Completing));

        var fresh = new YDoc();
        fresh.ApplyUpdate(doc.EncodeStateAsUpdate());
        Assert.AreEqual((1, null), (dropped, doc.Store.Pending));
    }

    /// <summary>A document holding the first two updates of the test above, whose delete ranges wait.</summary>
    private static YDoc WithWaitingRanges(YDocOptions options)
    {
        var doc = new YDoc(options);
        doc.ApplyUpdate(Convert.FromHexString("0103F8FAFFFF070601000C00022201016D016D02013103226A2222000C06016B02013103226A22020C010507F8FAFFFF070207020302"));
        doc.ApplyUpdate(Convert.FromHexString("0303F8FAFFFF07002101016D016D0128000C06016B037D477D4775027D017701782201016D016D02013103226A22030A000004C40C070C070364656604000C0003646566020C06070101610104000C050378C3A9010C010602"));
        return doc;
    }

    /// <summary>
    /// v1, client 7: a map 7:0 at root "a" with a value of three numbers under key "k" (7:1 to 7:3), a map 7:4 at root "b",
    /// and the deleted ranges 7:2 (which splits the value) and 7:4 (which deletes the second map). The check asks the filter
    /// about the planned map 7:0, above the live part of the split value, and lets the update through.
    /// </summary>
    private static readonly byte[] SplitValueBelowAPlannedMap =
    [
        1, 3, 7, 0,
        0x07, 1, 1, (byte)'a', 1,
        0x28, 0, 7, 0, 1, (byte)'k', 3, 0x7D, 1, 0x7D, 1, 0x7D, 1,
        0x07, 1, 1, (byte)'b', 1,
        1, 7, 2, 2, 1, 4, 1,
    ];

    [TestMethod]
    public void ApplyUpdate_FilterKeepsTheViewOfAPlannedItemThatWasIntegrated_ReadsTheStoresStruct()
    {
        var views = KeptViews(out var options);
        var doc = new YDoc(options);

        doc.ApplyUpdate(SplitValueBelowAPlannedMap);

        var view = views.Single();
        Assert.AreEqual((new ItemId(7, 0), ItemKind.Type, 1, true), (view.Id, view.Kind, view.Length, view.Content.Single() == doc.Store.Find(view.Id).TypeOf));
    }

    [TestMethod]
    public void ApplyUpdate_FilterKeepsTheViewOfAPlannedItemThatWasSetAside_ReadThrows()
    {
        var views = KeptViews(out var options);
        var doc = WithWaitingRanges(options);

        doc.ApplyUpdate(Convert.FromHexString(Completing));

        var view = views.Single();
        Assert.AreEqual((new ItemId(12, 6), true), (view.Id, Throws(() => view.Kind)));
    }

    private static List<ItemView> KeptViews(out YDocOptions options)
    {
        var views = new List<ItemView>();
        options = new YDocOptions
        {
            GarbageCollectionFilter = view =>
            {
                if (view.IsDetached)
                {
                    views.Add(view);
                }

                return true;
            },
        };
        return views;
    }

    private static bool Throws(Func<object> read)
    {
        try
        {
            read();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
