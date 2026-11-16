using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// An update that would make the cleanup's garbage collection throw (a deleted type in which a key holds two live
/// items) is rejected before it is integrated. The updates are the property engine's minimal case: the first
/// gives a nested map's key "l" a value of three items, the second splits it with a deleted range and deletes the map.
/// </summary>
[TestClass]
public class GarbageCollectionCheckTests
{
    private const string Base = "AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=";
    private const string SplitAndDelete = "AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC";

    [TestMethod]
    public void ApplyUpdate_DeletesATypeWhoseKeyHasTwoLiveItems_RejectsAndLeavesTheDocumentUnchanged()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = true });
        doc.ApplyUpdate(Convert.FromBase64String(Base));
        var before = Convert.ToBase64String(doc.EncodeStateAsUpdate());

        var thrown = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromBase64String(SplitAndDelete)));

        Assert.AreEqual((before, true), (Convert.ToBase64String(doc.EncodeStateAsUpdate()), thrown.Message.Contains("unchanged", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ApplyUpdate_SameUpdatesWithoutGarbageCollection_AppliesThem()
    {
        // Yjs applies them too: without garbage collection the second live item of the key stays, unseen.
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.ApplyUpdate(Convert.FromBase64String(Base));

        doc.ApplyUpdate(Convert.FromBase64String(SplitAndDelete));

        Assert.IsFalse(doc.HasPending);
    }

    [TestMethod]
    public void ApplyUpdate_SplitWithoutDeletingTheType_AppliesIt()
    {
        // The value of key "l" (70000:2, three items) loses its middle item; the map it is in stays.
        var doc = new YDoc(new YDocOptions { GarbageCollection = true });
        doc.ApplyUpdate(Convert.FromBase64String(Base));

        doc.ApplyUpdate(DeleteRange(70000, 3, 1));

        Assert.IsTrue(doc.CreateSnapshot().DeleteSet.IsDeleted(new ItemId(70000, 3)));
    }

    /// <summary>A v1 update with no structs and one deleted range.</summary>
    private static byte[] DeleteRange(long client, long clock, long length)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(0);
        writer.WriteVarUint(1);
        writer.WriteVarUint((ulong)client);
        writer.WriteVarUint(1);
        writer.WriteVarUint((ulong)clock);
        writer.WriteVarUint((ulong)length);
        return writer.ToArray();
    }
}
