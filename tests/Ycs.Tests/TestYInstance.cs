using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace Ycs;

public class TestYInstance
{
    public TestConnector _tc { get; set; }
    public IDictionary<TestYInstance, Queue<byte[]>> _receiving { get; set; } = new Dictionary<TestYInstance, Queue<byte[]>>();

    public YDoc Doc { get; }

    public static implicit operator YDoc(TestYInstance instance) => instance.Doc;

    internal StructStore Store => Doc.Store;

    public event EventHandler<UpdateEventArgs> UpdatedV2
    {
        add => Doc.UpdatedV2 += value;
        remove => Doc.UpdatedV2 -= value;
    }

    public YArray GetArray(string name = "") => Doc.GetArray(name);

    public YMap GetMap(string name = "") => Doc.GetMap(name);

    public YText GetText(string name = "") => Doc.GetText(name);

    public void Transact(Action<Transaction> action, object origin = null) => Doc.Transact(action, origin);

    public void Destroy() => Doc.Destroy();

    public TestYInstance(TestConnector connector, int clientId, YDocOptions options)
    {
        Doc = new YDoc(options);
        Doc.ClientId = clientId;

        _tc = connector;
        _tc._allConns.Add(this);

        // Setup observe on local model.
        Doc.UpdatedV2 += (s, e) =>
        {
            if (e.Origin != _tc)
            {
                var message = new ArrayBufferWriter<byte>();
                SyncProtocol.WriteUpdate(message, e.Update.Span);
                BroadcastMessage(this, message.WrittenSpan.ToArray());
            }
        };

        Connect();
    }

    private void BroadcastMessage(TestYInstance sender, byte[] data)
    {
        if (_tc._onlineConns.Contains(sender))
        {
            foreach (var conn in _tc._onlineConns)
            {
                if (sender != conn)
                {
                    conn.Receive(data, sender);
                }
            }
        }
    }

    public void Connect()
    {
        if (!_tc._onlineConns.Contains(this))
        {
            _tc._onlineConns.Add(this);

            {
                var stream = new ArrayBufferWriter<byte>();
                SyncProtocol.WriteSyncStep1(stream, Doc);

                // Publish SyncStep1
                BroadcastMessage(this, stream.WrittenSpan.ToArray());

                foreach (var remoteYInstance in _tc._onlineConns)
                {
                    if (remoteYInstance != this)
                    {
                        stream.Clear();
                        SyncProtocol.WriteSyncStep1(stream, remoteYInstance.Doc);

                        Receive(stream.WrittenSpan.ToArray(), remoteYInstance);
                    }
                }
            }
        }
    }

    public void Receive(byte[] data, TestYInstance remoteDoc)
    {
        if (!_receiving.TryGetValue(remoteDoc, out var messages))
        {
            messages = new Queue<byte[]>();
            _receiving[remoteDoc] = messages;
        }

        messages.Enqueue(data);
    }

    public void Disconnect()
    {
        _receiving.Clear();
        _tc._onlineConns.Remove(this);
    }
}
