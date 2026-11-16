using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ycs.Server.Rooms;
using Ycs.Server.Transports;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// A client of <see cref="SyncHub"/> over a raw WebSocket speaking SignalR's JSON protocol (no negotiate, as the JS
/// client with <c>skipNegotiation</c>): the same document behaviour as <see cref="SocketClient"/>, its messages
/// carried as base64 arguments of hub invocations. Each SignalR message ends with the record separator 0x1E.
/// </summary>
internal sealed class SignalRClient : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    private readonly ClientWebSocket socket = new();
    private readonly Channel<string> outbox = Channel.CreateUnbounded<string>();
    private readonly Lock gate = new();
    private readonly Server.Awareness.Awareness awareness = new(TimeProvider.System);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiving;
    private Task? sending;

    private SignalRClient(long clientId)
    {
        Doc = new YDoc(new YDocOptions { ClientId = clientId });
        Doc.Update += (_, e) =>
        {
            if (!ReferenceEquals(e.origin, this))
            {
                Send(Messages.Update(e.data));
            }
        };
    }

    /// <summary>Under <see cref="gate"/>, as every member of the document.</summary>
    public YDoc Doc { get; }

    /// <summary>Completes when the connection ends: a SignalR close message, a close frame, or a broken socket.</summary>
    public Task Closed => closed.Task;

    public static Uri HubUri(ServerHost server, string room) => new(server.WebSocketBase, $"/hub?room={Uri.EscapeDataString(room)}");

    /// <summary>Connects, completes the SignalR handshake and sends sync step 1, as a provider does on open.</summary>
    public static async Task<SignalRClient> ConnectAsync(Uri uri, long clientId)
    {
        var client = new SignalRClient(clientId);
        await client.socket.ConnectAsync(uri, CancellationToken.None).WaitAsync(SocketClient.Timeout);
        var handshake = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + RecordSeparator);
        await client.socket.SendAsync(handshake, WebSocketMessageType.Text, true, CancellationToken.None);
        client.receiving = client.ReceiveLoopAsync();
        client.sending = client.SendLoopAsync();
        lock (client.gate)
        {
            client.Send(Messages.Step1(client.Doc));
        }

        return client;
    }

    public string Text
    {
        get
        {
            lock (gate)
            {
                return Doc.GetText("text").ToString();
            }
        }
    }

    public void Insert(int index, string text)
    {
        lock (gate)
        {
            Doc.GetText("text").Insert(index, text);
        }
    }

    public bool HasAwareness(ulong clientId, out string state)
    {
        lock (gate)
        {
            return awareness.TryGetState(clientId, out state);
        }
    }

    /// <summary>Invokes the hub's <c>Send</c> with one y-protocols message.</summary>
    public void Send(byte[] message) =>
        SendRaw($"{{\"type\":1,\"target\":\"Send\",\"arguments\":[\"{Convert.ToBase64String(message)}\"]}}");

    /// <summary>Sends one SignalR message as given; the record separator is appended.</summary>
    public void SendRaw(string json) => outbox.Writer.TryWrite(json + RecordSeparator);

    /// <summary>Waits until <paramref name="condition"/> holds after some message arrived, or fails after 5 s.</summary>
    public async Task WaitUntilAsync(Func<SignalRClient, bool> condition)
    {
        using var timeout = new CancellationTokenSource(SocketClient.Timeout);
        while (true)
        {
            Task next;
            lock (gate)
            {
                if (condition(this))
                {
                    return;
                }

                next = changed.Task;
            }

            await next.WaitAsync(timeout.Token);
        }
    }

    /// <summary>Drops the TCP connection without a close message, as a client that vanishes.</summary>
    public void Abort() => socket.Abort();

    public async ValueTask DisposeAsync()
    {
        outbox.Writer.TryComplete();
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(SocketClient.Timeout);
            }
            catch (Exception e) when (e is WebSocketException or IOException or TimeoutException)
            {
                socket.Abort();
            }
        }
        else
        {
            socket.Abort();
        }

        await Task.WhenAll(receiving ?? Task.CompletedTask, sending ?? Task.CompletedTask);
        socket.Dispose();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var text in outbox.Reader.ReadAllAsync())
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var pending = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var handshaken = false;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                // A WebSocket message may hold several SignalR messages, or part of one.
                pending.Write(chunk, 0, result.Count);
                var buffer = pending.GetBuffer().AsSpan(0, (int)pending.Length);
                int end;
                while ((end = buffer.IndexOf((byte)RecordSeparator)) >= 0)
                {
                    var record = buffer[..end].ToArray();
                    buffer = buffer[(end + 1)..];
                    if (!handshaken)
                    {
                        // The handshake response: {} or {"error":"..."}.
                        handshaken = true;
                        Assert.AreEqual("{}", Encoding.UTF8.GetString(record));
                    }
                    else if (!Handle(record))
                    {
                        return;
                    }
                }

                var rest = buffer.ToArray();
                pending.SetLength(0);
                pending.Write(rest);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lock (gate)
            {
                closed.TrySetResult();
                changed.TrySetResult();
            }
        }
    }

    // Returns false for a close message.
    private bool Handle(byte[] record)
    {
        using var json = JsonDocument.Parse(record);
        var type = json.RootElement.GetProperty("type").GetInt32();
        if (type == 7)
        {
            return false;
        }

        if (type != 1)
        {
            // A ping (6) or anything else the hub does not send to this client.
            return true;
        }

        Assert.AreEqual(SyncHub.ClientMethod, json.RootElement.GetProperty("target").GetString());
        var message = json.RootElement.GetProperty("arguments")[0].GetBytesFromBase64();
        lock (gate)
        {
            var (kind, reader) = Messages.Split(message);
            if (kind == MessageType.Sync)
            {
                var reply = new MemoryStream();
                VarUint.Write(reply, MessageType.Sync);
                SyncProtocol.ReadSyncMessage(reader, reply, Doc, this);
                if (reply.Length > 1)
                {
                    Send(reply.ToArray());
                }
            }
            else if (kind == MessageType.Awareness)
            {
                awareness.ApplyUpdate(Messages.AwarenessUpdate(message));
            }

            var previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        return true;
    }
}
