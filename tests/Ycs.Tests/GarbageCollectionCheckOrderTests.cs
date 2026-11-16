using System;
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

    [TestMethod]
    [Ignore("Known bug: CheckGarbageCollection takes a planned value that a later planned item replaces for no value, and misses the split.")]
    public void ApplyUpdate_ValueSplitByItsReplacerInTheSameUpdateAndItsTypeDeleted_RejectsAndLeavesTheDocumentUnchanged()
    {
        var doc = new YDoc();
        var before = Convert.ToBase64String(doc.EncodeStateAsUpdate());

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromHexString(ReplacedAndDeleted)));

        Assert.AreEqual(before, Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    [Ignore("Known bug: the same cause, with the delete range waiting from an earlier update.")]
    public void ApplyUpdate_WaitingDeleteRangeAppliesToASplitValue_Rejects()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(Convert.FromHexString(WaitingDeletes));

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromHexString(Structs)));
    }

    [TestMethod]
    [Ignore("Known bug: AddApplicableRanges lists the waiting clients in insertion order, TakeApplicable applies them from the highest id down.")]
    public void ApplyUpdate_WaitingRangesOfTwoClients_CheckSeesThemInTheOrderTheyAreApplied()
    {
        // The property engine's seed 1302 in reversed order: the update that completes the structs finds the ranges of
        // client 12 (deleting the type 12:6) and of client 2147483000 (splitting its value) waiting.
        var doc = new YDoc();
        doc.ApplyUpdate(Convert.FromHexString("0103F8FAFFFF070601000C00022201016D016D02013103226A2222000C06016B02013103226A22020C010507F8FAFFFF070207020302"));
        doc.ApplyUpdate(Convert.FromHexString("0303F8FAFFFF07002101016D016D0128000C06016B037D477D4775027D017701782201016D016D02013103226A22030A000004C40C070C070364656604000C0003646566020C06070101610104000C050378C3A9010C010602"));

        doc.ApplyUpdate(Convert.FromHexString("02040C0007010178062101016D016B0204000C000378C3A9870C0006020B0000052801016D016B027B3FF80000000000007B3FF8000000000000020B02050104010C010302"));

        Assert.IsFalse(doc.HasPending);
    }
}
