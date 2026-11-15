using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A subdocument read from an update is created when it is integrated, with its parent's limits,
/// not while the update is decoded.
/// </summary>
[TestClass]
public class SubdocumentCostTests
{
    /// <summary>A v1 update of <paramref name="count"/> subdocuments in the root array "a", guid "g" and options {}.</summary>
    private static byte[] Subdocuments(int count, bool truncate = false)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint((uint)count);
        writer.WriteVarUint(7);
        writer.WriteVarUint(0);
        for (var i = 0; i < count; i++)
        {
            writer.WriteByte(9); // ContentDoc, no origins
            writer.WriteVarUint(1);
            writer.WriteVarString("a");
            writer.WriteVarString("g");
            writer.WriteByte(118); // an empty object
            writer.WriteVarUint(0);
        }

        if (!truncate)
        {
            writer.WriteVarUint(0);
        }

        return writer.ToArray();
    }

    [TestMethod]
    public void ApplyUpdate_RejectedUpdateOfSubdocuments_CreatesNoDocument()
    {
        var update = Subdocuments(10_000, truncate: true);
        var doc = new YDoc();

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        Assert.IsFalse(doc.TryApplyUpdate(update));
        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        // With its document a decoded subdocument costs about 1.6 KB, without it about 460 bytes.
        Assert.IsLessThan(10_000L * 800, allocated);
    }

    [TestMethod]
    public void ApplyUpdate_Subdocument_TakesTheParentsLimits()
    {
        var limits = new UpdateLimits { MaxStructsPerUpdate = 1_000 };
        var doc = new YDoc(new YDocOptions { Limits = limits });

        doc.ApplyUpdate(Subdocuments(1));

        Assert.AreSame(limits, ((YDoc)doc.GetArray("a").Get(0)).Limits);
    }

    [TestMethod]
    public void Destroy_Subdocument_ItsReplacementKeepsTheLimits()
    {
        var limits = new UpdateLimits { MaxStructsPerUpdate = 1_000 };
        var doc = new YDoc(new YDocOptions { Limits = limits });
        doc.ApplyUpdate(Subdocuments(1));

        ((YDoc)doc.GetArray("a").Get(0)).Destroy();

        Assert.AreSame(limits, ((YDoc)doc.GetArray("a").Get(0)).Limits);
    }
}

/// <summary>Subdocument options as an odd or hostile peer writes them, read as Yjs reads them.</summary>
[TestClass]
public class SubdocumentOptionTests
{
    /// <summary>A v1 update: client 7 sets "d" of the root map "m" to a subdocument "sub" with the options <paramref name="writeOptions"/> write.</summary>
    private static byte[] SubdocumentWith(Action<Lib0Writer> writeOptions)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint(7);
        writer.WriteVarUint(0);
        writer.WriteByte(9 | 32); // ContentDoc with a parentSub, no origins
        writer.WriteVarUint(1);
        writer.WriteVarString("m");
        writer.WriteVarString("d");
        writer.WriteVarString("sub");
        writeOptions(writer);
        writer.WriteVarUint(0);
        return writer.ToArray();
    }

    private static Action<Lib0Writer> OneOption(string key, Action<Lib0Writer> writeValue) => w =>
    {
        w.WriteByte(118);
        w.WriteVarUint(1);
        w.WriteVarString(key);
        writeValue(w);
    };

    private static readonly Action<Lib0Writer> Undefined = w => w.WriteByte(127);

    [TestMethod]
    public void ApplyUpdate_GcUndefined_IsTheDefaultAsInYjs()
    {
        // Yjs 13.6.32 (js/gcundef.mjs of the second review): { gc: undefined } reads as gc true and re-encodes as {}.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.ApplyUpdate(SubdocumentWith(OneOption("gc", Undefined)));

        Assert.AreEqual("010107002901016D016403737562760000", Convert.ToHexString(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    [DataRow("autoLoad")]
    [DataRow("shouldLoad")]
    [DataRow("meta")]
    [DataRow("collectionid")]
    public void ApplyUpdate_OptionUndefined_IsAbsent(string key)
    {
        var doc = new YDoc();

        doc.ApplyUpdate(SubdocumentWith(OneOption(key, Undefined)));

        var sub = (YDoc)doc.GetMap("m").Get("d");
        Assert.AreEqual((false, false, null, null), (sub.AutoLoad, sub.ShouldLoad, sub.Meta, sub.CollectionId));
    }

    [TestMethod]
    public void ApplyUpdate_GuidUndefined_IsRejected()
    {
        // Yjs draws a random guid, so its peers would not agree on the subdocument.
        Assert.IsFalse(new YDoc().TryApplyUpdate(SubdocumentWith(OneOption("guid", Undefined))));
    }

    [TestMethod]
    public void ApplyUpdate_CollectionIdNotAString_IsIgnored()
    {
        var doc = new YDoc();

        doc.ApplyUpdate(SubdocumentWith(OneOption("collectionid", w => w.WriteAny(1))));

        Assert.IsNull(((YDoc)doc.GetMap("m").Get("d")).CollectionId);
    }
}
