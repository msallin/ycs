using System;
using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The span and <see cref="UpdateFormat"/> entry points of the document, snapshots and <see cref="YUpdate"/>.</summary>
[TestClass]
public class EncodingEntryPointTests
{
    private static YDoc Doc(int clientId, string text)
    {
        var doc = new YDoc(new YDocOptions { ClientId = clientId, Gc = false });
        doc.GetText("t").Insert(0, text);
        return doc;
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void EncodeStateAsUpdate_IntoABufferWriter_WritesTheBytesOfTheArrayOverload(UpdateFormat format)
    {
        var doc = Doc(1, "hello");
        var writer = new ArrayBufferWriter<byte>();

        doc.EncodeStateAsUpdate(writer, format: format);

        CollectionAssert.AreEqual(doc.EncodeStateAsUpdate(format: format), writer.WrittenSpan.ToArray());
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void ApplyUpdate_InEitherFormat_ReproducesTheDocument(UpdateFormat format)
    {
        var source = Doc(1, "hello");
        var target = new YDoc();

        target.ApplyUpdate(source.EncodeStateAsUpdate(format: format), format: format);

        Assert.AreEqual("hello", target.GetText("t").ToString());
    }

    [TestMethod]
    public void TryApplyUpdate_MalformedUpdate_ReturnsFalse()
    {
        Assert.IsFalse(new YDoc().TryApplyUpdate(new byte[] { 255, 255, 255 }, format: UpdateFormat.V2));
    }

    [TestMethod]
    public void ApplyUpdate_UndefinedFormat_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new YDoc().ApplyUpdate(new byte[] { 0, 0 }, format: (UpdateFormat)7));
    }

    [TestMethod]
    public void Convert_V1ToV2AndBack_ReturnsTheV1Bytes()
    {
        var v1 = Doc(1, "hello").EncodeStateAsUpdate();

        var back = YUpdate.Convert(YUpdate.Convert(v1, UpdateFormat.V1, UpdateFormat.V2), UpdateFormat.V2, UpdateFormat.V1);

        CollectionAssert.AreEqual(v1, back);
    }

    [TestMethod]
    public void Merge_SingleUpdate_ReturnsACopy()
    {
        var update = Doc(1, "hello").EncodeStateAsUpdate();

        var merged = YUpdate.Merge([update]);

        Assert.IsTrue(update.AsSpan().SequenceEqual(merged) && !ReferenceEquals(update, merged));
    }

    [TestMethod]
    public void Merge_TwoUpdates_AppliesToTheUnion()
    {
        var a = Doc(1, "a").EncodeStateAsUpdate();
        var b = Doc(2, "b").EncodeStateAsUpdate();
        var target = new YDoc();

        target.ApplyUpdate(YUpdate.Merge([a, b], UpdateFormat.V1));

        Assert.AreEqual(2, YUpdate.ParseMeta(YUpdate.Merge([a, b])).From.Count);
        Assert.AreEqual(2, target.GetText("t").Length);
    }

    [TestMethod]
    public void Merge_OneMalformedUpdate_ThrowsYUpdateException()
    {
        var a = Doc(1, "a").EncodeStateAsUpdate();

        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([a, new byte[] { 200, 200 }]));
    }

    [TestMethod]
    public void Merge_UndefinedFormat_Throws()
    {
        var a = Doc(1, "a").EncodeStateAsUpdate();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => YUpdate.Merge([a], (UpdateFormat)3));
    }

    [TestMethod]
    public void Diff_AgainstTheStateVectorOfTheSameUpdate_HoldsNoStructs()
    {
        var update = Doc(1, "hello").EncodeStateAsUpdate(format: UpdateFormat.V2);
        var vector = YUpdate.EncodeStateVector(update, UpdateFormat.V2);

        var diff = YUpdate.Diff(update, vector, UpdateFormat.V2);

        Assert.AreEqual(0, YUpdate.Decode(diff, UpdateFormat.V2).Structs.Count);
    }

    [TestMethod]
    public void Describe_AnUpdate_ListsItsStructsAndDeleteSet()
    {
        var text = YUpdate.Describe(Doc(1, "hello").EncodeStateAsUpdate());

        StringAssert.Contains(text, "Structs (1):");
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void Snapshot_EncodeToABufferWriter_EqualsTheArrayAndDecodesToAnEqualSnapshot(UpdateFormat format)
    {
        var snapshot = Doc(1, "hello").CreateSnapshot();
        var writer = new ArrayBufferWriter<byte>();

        snapshot.Encode(writer, format);

        CollectionAssert.AreEqual(snapshot.Encode(format), writer.WrittenSpan.ToArray());
        Assert.AreEqual(snapshot, Snapshot.Decode(writer.WrittenSpan, format));
    }

    [TestMethod]
    public void Snapshot_DecodeMalformedBytes_ThrowsYUpdateException()
    {
        Assert.ThrowsExactly<YUpdateException>(() => Snapshot.Decode(new byte[] { 9, 9, 9, 9 }));
    }

    [TestMethod]
    public void Snapshot_ContainsUpdate_V2OfAPrefix_IsTrue()
    {
        var doc = Doc(1, "ab");
        var snapshot = doc.CreateSnapshot();

        Assert.IsTrue(snapshot.ContainsUpdate(doc.EncodeStateAsUpdate(format: UpdateFormat.V2), UpdateFormat.V2));
    }

    [TestMethod]
    public void SyncProtocol_V2_RoundTripsStepTwo()
    {
        var source = Doc(1, "hello");
        var target = new YDoc();
        var message = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteSyncStep2(message, source, target.EncodeStateVector(), UpdateFormat.V2);

        SyncProtocol.ReadSyncMessage(message.WrittenSpan, new ArrayBufferWriter<byte>(), target, null, UpdateFormat.V2);

        Assert.AreEqual("hello", target.GetText("t").ToString());
    }
}
