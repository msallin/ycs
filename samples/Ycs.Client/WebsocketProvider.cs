using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Ycs.Client;

/// <summary>
/// y-websocket's provider over <see cref="ClientWebSocket"/>: keeps <see cref="Doc"/> in sync with a room of a
/// y-websocket server and keeps this client's awareness state there. It connects to <c>{serverUrl}/{room}</c>, sends
/// sync step 1 and its awareness state, answers the server's step 1 with step 2, sends each local update as it
/// happens and applies the server's. After a drop it connects again with y-websocket's backoff and syncs again, so
/// edits made while disconnected merge.
/// </summary>
/// <remarks>
/// Every touch of <see cref="Doc"/> and of the awareness holds <see cref="Gate"/>: the receive loop applying a
/// message, the awareness timer, <see cref="SetLocalState"/>, <see cref="DisposeAsync"/>, and the owner's own reads and
/// edits of the document, which must take it too. The events are raised under it. In the browser there is one thread,
/// and the lock never waits.
/// </remarks>
internal sealed class WebsocketProvider : IAsyncDisposable
{
    /// <summary>The longest wait between two connection attempts (y-websocket's <c>maxBackoffTime</c>).</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(2500);

    private const int ReceiveChunk = 16 * 1024;

    // y-protocols checks its awareness every outdatedTimeout / 10: renewal of the own state, removal of outdated ones.
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(Server.Awareness.Awareness.OutdatedTimeoutMs / 10);

    // How long DisposeAsync waits for the server to answer the close frame.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeProvider time;
    private readonly ClientAwareness awareness;
    private readonly CancellationTokenSource stopping = new();
    private readonly ITimer timer;

    // The open connection, under the gate; null while there is none. Local updates go to it alone.
    private Connection? connection;
    private Task? running;
    private bool disposed;

    public WebsocketProvider(Uri serverUrl, string room, YDoc doc, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentException.ThrowIfNullOrEmpty(room);
        ArgumentNullException.ThrowIfNull(doc);
        this.time = time ?? TimeProvider.System;
        Url = new Uri(serverUrl.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(room));
        Doc = doc;
        awareness = new ClientAwareness((ulong)doc.ClientId, this.time);
        doc.Update += OnUpdate;
        timer = this.time.CreateTimer(static provider => ((WebsocketProvider)provider!).Check(), this, CheckEvery, CheckEvery);
    }

    /// <summary>Raised when <see cref="Status"/> or <see cref="Synced"/> changed.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised after a sync step 2 or an update from the server was applied to <see cref="Doc"/>.</summary>
    public event Action? Received;

    /// <summary>Raised when <see cref="RemoteStates"/> changed.</summary>
    public event Action? AwarenessChanged;

    public Lock Gate { get; } = new();

    public YDoc Doc { get; }

    /// <summary>The room's URL: the server URL, a slash and the room name, escaped.</summary>
    public Uri Url { get; }

    public ConnectionStatus Status { get; private set; }

    /// <summary>Whether the current connection has received the server's sync step 2.</summary>
    public bool Synced { get; private set; }

    /// <summary>This client's awareness id: the document's client id, as in y-protocols.</summary>
    public ulong ClientId => awareness.ClientId;

    /// <summary>This client's awareness state as JSON text; null when it has none.</summary>
    public string? LocalState => awareness.LocalState;

    /// <summary>The other clients in the room that have an awareness state, and their states as JSON text.</summary>
    public IReadOnlyList<(ulong ClientId, string State)> RemoteStates => [.. awareness.Remote];

    /// <summary>Starts connecting; from then on the provider stays connected, or tries to, until it is disposed.</summary>
    public void Connect()
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            running ??= RunAsync();
        }
    }

    /// <summary>Sets this client's awareness state (JSON text, null for none) and sends it if connected.</summary>
    public void SetLocalState(string? json)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var message = awareness.SetLocalState(json);
            connection?.Send(message);
        }
    }

    /// <summary>
    /// Leaves: sends the null awareness state and what is still queued, closes the connection (waiting up to 2 s for
    /// the server's answer) and stops reconnecting. The document stays as it is.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (Gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Doc.Update -= OnUpdate;
            if (connection is { } open)
            {
                if (awareness.LocalState is not null)
                {
                    open.Send(awareness.SetLocalState(null));
                }

                // The send loop sends what is queued, then the close frame; the receive loop ends with the answer.
                open.Outbox.Writer.TryComplete();
                stopping.CancelAfter(CloseTimeout);
            }
            else
            {
                stopping.Cancel();
            }

            run = running;
        }

        await timer.DisposeAsync();
        if (run is not null)
        {
            await run;
        }

        stopping.Dispose();
    }

    // y-websocket's backoff: 100 ms after a drop, doubling with each attempt that did not connect, at most MaxBackoff.
    private static TimeSpan Backoff(int failedAttempts) =>
        TimeSpan.FromMilliseconds(Math.Min(100 * Math.Pow(2, failedAttempts), MaxBackoff.TotalMilliseconds));

    private static async Task SendLoopAsync(Connection connection)
    {
        var socket = connection.Socket;
        try
        {
            await foreach (var message in connection.Outbox.Reader.ReadAllAsync())
            {
                await socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            socket.Abort();
        }
    }

    private bool IsDisposed
    {
        get
        {
            lock (Gate)
            {
                return disposed;
            }
        }
    }

    private async Task RunAsync()
    {
        var failedAttempts = 0;
        while (!IsDisposed)
        {
            SetStatus(ConnectionStatus.Connecting);
            using (var socket = new ClientWebSocket())
            {
                if (await TryConnectAsync(socket))
                {
                    failedAttempts = 0;
                    await RunConnectionAsync(socket);
                }
                else
                {
                    failedAttempts++;
                }
            }

            SetStatus(ConnectionStatus.Disconnected);
            if (IsDisposed)
            {
                break;
            }

            try
            {
                await Task.Delay(Backoff(failedAttempts), time, stopping.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> TryConnectAsync(ClientWebSocket socket)
    {
        try
        {
            await socket.ConnectAsync(Url, stopping.Token);
            return true;
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunConnectionAsync(ClientWebSocket socket)
    {
        var open = new Connection(socket);
        lock (Gate)
        {
            if (disposed)
            {
                return;
            }

            connection = open;
            open.Send(Wire.SyncStep1(Doc));

            // With the next clock: the server keeps the clock of a state it removed on close, and would ignore the
            // same clock again until the renewal.
            if (awareness.LocalState is { } state)
            {
                open.Send(awareness.SetLocalState(state));
            }

            SetStatus(ConnectionStatus.Connected);
        }

        var sending = SendLoopAsync(open);
        try
        {
            await ReceiveLoopAsync(open);
        }
        finally
        {
            lock (Gate)
            {
                connection = null;
                open.Outbox.Writer.TryComplete();
                if (Synced)
                {
                    Synced = false;
                    StatusChanged?.Invoke();
                }

                if (!awareness.Forget().IsEmpty)
                {
                    AwarenessChanged?.Invoke();
                }
            }

            await sending;
        }
    }

    private async Task ReceiveLoopAsync(Connection open)
    {
        var buffer = new ArrayBufferWriter<byte>(ReceiveChunk);
        try
        {
            while (true)
            {
                var result = await open.Socket.ReceiveAsync(buffer.GetMemory(ReceiveChunk), stopping.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                buffer.Advance(result.Count);
                if (result.EndOfMessage)
                {
                    if (!Handle(open, buffer.WrittenSpan.ToArray()))
                    {
                        open.Socket.Abort();
                        return;
                    }

                    buffer.ResetWrittenCount();
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            open.Socket.Abort();
        }
    }

    // False for a message this client cannot read or apply: the connection drops, and the next one syncs again.
    private bool Handle(Connection open, byte[] message)
    {
        lock (Gate)
        {
            try
            {
                var reader = new MemoryStream(message, writable: false);
                switch (Wire.ReadVarUint(reader))
                {
                    case Wire.Sync:
                        HandleSync(open, message.AsSpan((int)reader.Position));
                        break;
                    case Wire.Awareness:
                        HandleAwareness(open, reader);
                        break;
                    default:
                        // y-websocket's auth (2) and query awareness (3): this server sends neither.
                        break;
                }

                return true;
            }
            catch (YUpdateException)
            {
                return false;
            }
        }
    }

    // Under the gate.
    private void HandleSync(Connection open, ReadOnlySpan<byte> message)
    {
        var reply = new ArrayBufferWriter<byte>();
        reply.Write([(byte)Wire.Sync]);
        var type = SyncProtocol.ReadSyncMessage(message, reply, Doc, this);

        // Only a step 1 writes a reply; the outer type alone is one byte.
        if (reply.WrittenCount > 1)
        {
            open.Send(reply.WrittenSpan.ToArray());
            return;
        }

        Received?.Invoke();
        if (type == SyncMessageType.SyncStep2 && !Synced)
        {
            Synced = true;
            StatusChanged?.Invoke();
        }
    }

    // Under the gate.
    private void HandleAwareness(Connection open, MemoryStream reader)
    {
        var (change, reply) = awareness.ApplyUpdate(Wire.ReadVarUint8Array(reader));
        if (reply is not null)
        {
            open.Send(reply);
        }

        if (change.Broadcast().Any(id => id != ClientId))
        {
            AwarenessChanged?.Invoke();
        }
    }

    // Raised inside the transaction that made the update, whose author holds the gate.
    private void OnUpdate(object? sender, (byte[] data, object origin, Transaction transaction) e)
    {
        if (!ReferenceEquals(e.origin, this))
        {
            connection?.Send(Wire.Update(e.data));
        }
    }

    private void Check()
    {
        lock (Gate)
        {
            if (disposed)
            {
                return;
            }

            if (awareness.RenewIfDue() is { } renewal)
            {
                connection?.Send(renewal);
            }

            if (!awareness.RemoveOutdated().IsEmpty)
            {
                AwarenessChanged?.Invoke();
            }
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        lock (Gate)
        {
            if (Status != status)
            {
                Status = status;
                StatusChanged?.Invoke();
            }
        }
    }

    /// <summary>One socket and the messages queued for it; its send loop is the only writer of the socket.</summary>
    private sealed class Connection(ClientWebSocket socket)
    {
        public ClientWebSocket Socket => socket;

        public Channel<byte[]> Outbox { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public void Send(byte[] message) => Outbox.Writer.TryWrite(message);
    }
}
