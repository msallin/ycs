using System.Net.WebSockets;
using System.Threading.Channels;
using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// A y-websocket client over a real socket: a <see cref="YDoc"/> whose local updates are sent, that answers sync
/// messages as y-websocket's provider does, and that keeps the awareness states it is told about. Tests wait on
/// conditions with <see cref="WaitUntilAsync"/>.
/// </summary>
internal sealed class SocketClient : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket socket = new();
    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> outbox = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly Lock gate = new();
    private readonly Server.Awareness.Awareness awareness = new(TimeProvider.System);
    private readonly TaskCompletionSource<WebSocketCloseStatus?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiving;
    private Task? sending;

    private SocketClient(long clientId)
    {
        Doc = new YDoc(new YDocOptions { ClientId = clientId });
        Doc.Updated += (_, e) =>
        {
            if (!ReferenceEquals(e.Origin, this))
            {
                outbox.Writer.TryWrite((Messages.Update(e.Update.ToArray()), WebSocketMessageType.Binary));
            }
        };
    }

    /// <summary>Under <see cref="gate"/>, as every member of the document.</summary>
    public YDoc Doc { get; }

    /// <summary>The close status the server sent; completes with null if the connection broke without one.</summary>
    public Task<WebSocketCloseStatus?> Closed => closed.Task;

    public static async Task<SocketClient> ConnectAsync(Uri uri, long clientId)
    {
        var client = new SocketClient(clientId);
        await client.socket.ConnectAsync(uri, CancellationToken.None).WaitAsync(Timeout);
        client.receiving = client.ReceiveLoopAsync();
        client.sending = client.SendLoopAsync();

        // As y-websocket's provider on open: sync step 1.
        lock (client.gate)
        {
            client.outbox.Writer.TryWrite((Messages.Step1(client.Doc), WebSocketMessageType.Binary));
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

    public void Send(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary) => outbox.Writer.TryWrite((data, type));

    /// <summary>Waits until <paramref name="condition"/> holds after some message arrived, or fails after 5 s.</summary>
    public async Task WaitUntilAsync(Func<SocketClient, bool> condition)
    {
        using var timeout = new CancellationTokenSource(Timeout);
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

    /// <summary>Drops the TCP connection without a close frame, as a client that vanishes.</summary>
    public void Abort() => socket.Abort();

    public async ValueTask DisposeAsync()
    {
        outbox.Writer.TryComplete();
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(Timeout);
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
            await foreach (var (data, type) in outbox.Reader.ReadAllAsync())
            {
                await socket.SendAsync(data, type, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed.TrySetResult(result.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                buffer.Write(chunk, 0, result.Count);
                if (result.EndOfMessage)
                {
                    Handle(buffer.ToArray());
                    buffer.SetLength(0);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            closed.TrySetResult(null);
        }
        finally
        {
            lock (gate)
            {
                changed.TrySetResult();
            }
        }
    }

    private void Handle(byte[] message)
    {
        lock (gate)
        {
            var (type, reader) = Messages.Split(message);
            if (type == MessageType.Sync)
            {
                if (Messages.HandleSync(reader, Doc, this) is { } answer)
                {
                    outbox.Writer.TryWrite((answer, WebSocketMessageType.Binary));
                }
            }
            else if (type == MessageType.Awareness)
            {
                awareness.ApplyUpdate(Messages.AwarenessUpdate(message));
            }

            var previous = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
}
