using Microsoft.AspNetCore.SignalR;
using Ycs.Server.Rooms;

namespace Ycs.Server.Transports;

/// <summary>
/// The SignalR transport: <c>/hub?room=...</c>, SignalR's JSON protocol, the same y-protocols messages as the
/// WebSocket transport. The client sends one message per invocation of <see cref="Send"/> (a <c>byte[]</c>, base64
/// in JSON); the server sends one per invocation of the client method <see cref="ClientMethod"/>, in order.
/// </summary>
internal sealed partial class SyncHub(
    RoomRegistry rooms,
    IHubContext<SyncHub> hub,
    WebSocketEndpointOptions options,
    ILogger<SyncHub> logger) : Hub
{
    public const string ClientMethod = "message";

    public const string RoomParameter = "room";

    // The room is in the URL, not in a first Join call: the room is known when the connection opens, so the session
    // starts there as on the WebSocket transport, and no message can arrive before it has a room.
    internal static string? RoomName(HttpContext? context)
    {
        var values = context?.Request.Query[RoomParameter] ?? default;
        return values.Count == 1 && values[0] is { Length: >= 1 and <= WebSocketEndpoint.MaxRoomNameLength } name ? name : null;
    }

    /// <summary>
    /// The largest SignalR frame a client may send: an invocation of <see cref="Send"/> with a message of
    /// <paramref name="maxMessageBytes"/>, which base64 inflates by 4/3, plus the invocation's JSON around it.
    /// </summary>
    internal static long MaxFrameBytes(int maxMessageBytes) => (((long)maxMessageBytes + 2) / 3 * 4) + 1024;

    public override Task OnConnectedAsync()
    {
        var name = RoomName(Context.GetHttpContext())
            ?? throw new HubException("The room query parameter is missing or invalid.");
        var connection = new Connection(new Session(rooms.GetOrCreate(name)), Context);
        Context.Items[typeof(Connection)] = connection;
        connection.Session.Start();
        connection.Sending = SendLoopAsync(connection, hub.Clients.Client(Context.ConnectionId));
        LogOpened(logger, Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>One y-protocols message from the client.</summary>
    public void Send(byte[]? message)
    {
        var connection = (Connection)Context.Items[typeof(Connection)]!;
        if (message is null || message.Length > options.MaxMessageBytes)
        {
            connection.Abort(SyncHubCloseReason.MessageRejected);
            return;
        }

        // Session.Receive must not run concurrently with itself: SignalR runs one connection's invocations one at a
        // time (HubOptions.MaximumParallelInvocationsPerClient = 1, set in AddSyncHub).
        connection.Session.Receive(message);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connection = (Connection)Context.Items[typeof(Connection)]!;
        connection.Session.Close();
        await connection.Sending!;
        LogClosed(logger, Context.ConnectionId, connection.Session.CloseReason, connection.AbortReason);
    }

    // The only sender to its connection, so the client gets the session's messages in their order.
    private static async Task SendLoopAsync(Connection connection, ISingleClientProxy client)
    {
        var session = connection.Session;
        try
        {
            while (await session.Outgoing.WaitToReadAsync())
            {
                // A closed session's queue is dropped: the client resyncs when it reconnects.
                while (!session.IsClosed && session.Outgoing.TryRead(out var message))
                {
                    await client.SendAsync(ClientMethod, message);
                }

                if (session.IsClosed)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            session.Close();
        }

        if (session.CloseReason is SessionCloseReason.ProtocolError or SessionCloseReason.QueueFull)
        {
            connection.Abort(SyncHubCloseReason.SessionClosed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SignalR connection {ConnectionId} opened")]
    private static partial void LogOpened(ILogger logger, string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SignalR connection {ConnectionId} closed: {Reason}, aborted {Abort}")]
    private static partial void LogClosed(ILogger logger, string connectionId, SessionCloseReason reason, SyncHubCloseReason abort);

    /// <summary>The state of one connection, kept in <see cref="HubCallerContext.Items"/> from connect to disconnect.</summary>
    private sealed class Connection(Session session, HubCallerContext context)
    {
        private int abortReason;

        public Session Session => session;

        /// <summary>Written once in OnConnectedAsync, before any invocation or the disconnect can read it.</summary>
        public Task? Sending { get; set; }

        public SyncHubCloseReason AbortReason => (SyncHubCloseReason)Volatile.Read(ref abortReason);

        public void Abort(SyncHubCloseReason reason)
        {
            Interlocked.CompareExchange(ref abortReason, (int)reason, (int)SyncHubCloseReason.None);
            session.Close();
            context.Abort();
        }
    }
}
