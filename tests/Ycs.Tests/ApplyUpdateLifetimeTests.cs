using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Nothing decoded from an update refers to its bytes once the call returns.</summary>
[TestClass]
public class ApplyUpdateLifetimeTests
{
    private static List<byte[]> TwoDependentUpdates()
    {
        var updates = new List<byte[]>();
        var author = new YDoc();
        author.Updated += (_, e) => updates.Add(e.Update.ToArray());
        var text = author.GetText("t");
        text.Insert(0, "first ");
        text.Insert(6, "second");
        return updates;
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void ApplyUpdate_FromArraysOverwrittenAfterwards_KeepsTheDocumentAndWhatWasDecoded(UpdateFormat format)
    {
        var updates = TwoDependentUpdates();
        var second = YUpdate.Convert(updates[1], UpdateFormat.V1, format);
        var first = YUpdate.Convert(updates[0], UpdateFormat.V1, format);
        var expectedStructs = YUpdate.Decode(first, format).Structs.Count;
        var doc = new YDoc();
        doc.ApplyUpdate(second, format);
        var decoded = YUpdate.Decode(first, format).Structs;
        Array.Fill(second, (byte)0xFF);
        doc.ApplyUpdate(first, format);
        var snapshotBytes = doc.CreateSnapshot().Encode(format);
        var snapshot = Snapshot.Decode(snapshotBytes, format);

        Array.Fill(first, (byte)0xFF);
        Array.Fill(snapshotBytes, (byte)0xFF);

        Assert.AreEqual(
            ("first second", expectedStructs, true),
            (doc.GetText("t").ToString(), decoded.Count, snapshot.Equals(doc.CreateSnapshot())));
    }

    [TestMethod]
    public void ApplyUpdate_FromASpan_RentsNothingFromTheBufferPool()
    {
        var update = TwoDependentUpdates()[0];

        new YDoc().ApplyUpdate(update.AsSpan());

        Assert.AreEqual(0, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    public void ApplyUpdate_OfAnUpdateOf100KB_AllocatesFarLessThanTheUpdatePerCall()
    {
        var author = new YDoc();
        author.GetText("t").Insert(0, new string('x', 100_000));
        var update = author.EncodeStateAsUpdate();
        var doc = new YDoc();
        doc.ApplyUpdate(update.AsSpan());
        doc.ApplyUpdate(update.AsSpan());
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 20; i++)
        {
            doc.ApplyUpdate(update.AsSpan());
        }

        Assert.IsLessThan(20 * 8 * 1024, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
