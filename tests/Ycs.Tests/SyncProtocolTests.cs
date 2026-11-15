using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>y-protocols' readSyncStep2 / readUpdate: errors of the apply go to the error handler.</summary>
[TestClass]
public class SyncProtocolTests
{
    // An update message whose update is one byte that is not a valid update, followed by a valid update message.
    private static MemoryStream BrokenThenValidUpdate(out YDoc source)
    {
        source = new YDoc(new YDocOptions { ClientId = 7 });
        source.GetText("t").Insert(0, "ok");
        var stream = new MemoryStream();
        SyncProtocol.WriteUpdate(stream, new byte[] { 0xff });
        SyncProtocol.WriteUpdate(stream, source.EncodeStateAsUpdate());
        stream.Position = 0;
        return stream;
    }

    [TestMethod]
    public void ReadSyncMessage_MalformedUpdateWithErrorHandler_HandsItOverAndReadsTheNextMessage()
    {
        using var stream = BrokenThenValidUpdate(out _);
        var doc = new YDoc();
        var errors = new List<Exception>();

        SyncProtocol.ReadSyncMessage(stream, Stream.Null, doc, "peer", errorHandler: errors.Add);
        SyncProtocol.ReadSyncMessage(stream, Stream.Null, doc, "peer", errorHandler: errors.Add);

        Assert.AreEqual((1, true, "ok"), (errors.Count, errors[0] is YUpdateException, doc.GetText("t").ToString()));
    }

    [TestMethod]
    public void ReadSyncMessage_MalformedUpdateWithoutErrorHandler_Throws()
    {
        using var stream = BrokenThenValidUpdate(out _);

        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadSyncMessage(stream, Stream.Null, new YDoc(), "peer"));
    }

    [TestMethod]
    public void ReadUpdate_ObserverThrows_HandsItOver()
    {
        using var stream = BrokenThenValidUpdate(out _);
        stream.Position = 4; // past the first message (type, length, one byte) and the type of the second
        var doc = new YDoc();
        doc.GetText("t").EventHandler += (_, _) => throw new InvalidOperationException("observer");
        Exception error = null;

        SyncProtocol.ReadUpdate(stream, doc, "peer", errorHandler: e => error = e);

        Assert.AreEqual(("observer", "ok"), (error?.Message, doc.GetText("t").ToString()));
    }
}
