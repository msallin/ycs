// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// The y-protocols 'sync' message format. Defaults to the original ('v1') update
/// encoding, which is what y-websocket and every other JS provider puts on the wire;
/// pass <c>v2: true</c> to use the denser v2 encoding on both ends instead.
/// </summary>
public static class SyncProtocol
{
    public const uint MessageYjsSyncStep1 = 0;
    public const uint MessageYjsSyncStep2 = 1;
    public const uint MessageYjsUpdate = 2;

    public static void WriteSyncStep1(Stream stream, YDoc doc, bool v2 = false)
    {
        stream.WriteVarUint(MessageYjsSyncStep1);
        var sv = v2 ? doc.EncodeStateVectorV2() : doc.EncodeStateVectorV1();
        stream.WriteVarUint8Array(sv);
    }

    public static void WriteSyncStep2(Stream stream, YDoc doc, byte[] encodedStateVector, bool v2 = false)
    {
        stream.WriteVarUint(MessageYjsSyncStep2);
        var update = v2 ? doc.EncodeStateAsUpdateV2(encodedStateVector) : doc.EncodeStateAsUpdateV1(encodedStateVector);
        stream.WriteVarUint8Array(update);
    }

    public static void ReadSyncStep1(Stream reader, Stream writer, YDoc doc, bool v2 = false)
    {
        var encodedStateVector = reader.ReadVarUint8Array();
        WriteSyncStep2(writer, doc, encodedStateVector, v2);
    }

    public static void ReadSyncStep2(Stream stream, YDoc doc, object transactionOrigin, bool v2 = false)
    {
        var update = stream.ReadVarUint8Array();
        if (v2)
        {
            doc.ApplyUpdateV2(update, transactionOrigin);
        }
        else
        {
            doc.ApplyUpdateV1(update, transactionOrigin);
        }
    }

    public static void WriteUpdate(Stream stream, byte[] update)
    {
        stream.WriteVarUint(MessageYjsUpdate);
        stream.WriteVarUint8Array(update);
    }

    public static void ReadUpdate(Stream stream, YDoc doc, object transactionOrigin, bool v2 = false)
    {
        ReadSyncStep2(stream, doc, transactionOrigin, v2);
    }

    public static uint ReadSyncMessage(Stream reader, Stream writer, YDoc doc, object transactionOrigin, bool v2 = false)
    {
        var messageType = reader.ReadVarUint();

        switch (messageType)
        {
            case MessageYjsSyncStep1:
                ReadSyncStep1(reader, writer, doc, v2);
                break;
            case MessageYjsSyncStep2:
                ReadSyncStep2(reader, doc, transactionOrigin, v2);
                break;
            case MessageYjsUpdate:
                ReadUpdate(reader, doc, transactionOrigin, v2);
                break;
            default:
                throw new Exception($"Unknown message type: {messageType}");
        }

        return messageType;
    }
}
