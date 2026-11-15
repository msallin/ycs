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
