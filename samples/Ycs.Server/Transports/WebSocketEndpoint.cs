using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Features;
using Ycs.Server.Rooms;

namespace Ycs.Server.Transports;

/// <summary>
/// The y-websocket transport: <c>/ws/{room}</c>, one binary WebSocket message per y-protocols message. y-websocket's
/// provider appends <c>/{roomname}</c> to its server URL, so clients connect to <c>ws://host/ws</c>.
/// </summary>
internal static partial class WebSocketEndpoint
{
    public const int MaxRoomNameLength = 256;

    // How long the server waits for the client's answer to its close frame before it drops the connection.
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    // RFC 6455 "Try Again Later"; WebSocketCloseStatus has no member for it.
    internal const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;

    private const int ReceiveChunk = 16 * 1024;

    public static IEndpointConventionBuilder MapWebSocketEndpoint(this IEndpointRouteBuilder endpoints, string pattern = "/ws/{room}") =>
        endpoints.Map(pattern, HandleAsync);

    private static async Task HandleAsync(HttpContext context)
    {
        var name = RoomName(context);
        if (!context.WebSockets.IsWebSocketRequest || name is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var services = context.RequestServices;
        var options = services.GetRequiredService<WebSocketEndpointOptions>();
        var logger = services.GetRequiredService<ILogger<Connection>>();
        var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var room = services.GetRequiredService<RoomRegistry>().GetOrCreate(name);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new Connection(socket, new Session(room), options.MaxMessageBytes);
        LogOpened(logger, context.Connection.Id);
        var status = await connection.RunAsync(stopping);
        LogClosed(logger, context.Connection.Id, connection.Session.CloseReason, status);
    }

    // The path segment as sent, decoded once: the route value would already be decoded except for "%2F".
    private static string? RoomName(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? string.Empty;
        var query = raw.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            raw = raw[..query];
        }

        var name = Uri.UnescapeDataString(raw[(raw.LastIndexOf('/') + 1)..]);
        return name.Length is >= 1 and <= MaxRoomNameLength ? name : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} opened")]
    private static partial void LogOpened(ILogger logger, string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket connection {ConnectionId} closed: {Reason}, close status {Status}")]
    private static partial void LogClosed(ILogger logger, string connectionId, SessionCloseReason reason, WebSocketCloseStatus? status);

    /// <summary>
    /// One socket and its session: a receive loop feeding <see cref="Session.Receive"/> and a send loop draining
    /// <see cref="Session.Outgoing"/>. Whoever ends the connection closes the session; its outgoing queue then
    /// completes, and the send loop sends the close frame. The send loop is the only writer of the socket.
    /// </summary>
    internal sealed class Connection(WebSocket socket, Session session, int maxMessageBytes)
    {
        // Set by the receive loop before it closes the session, for a close the session has no reason for.
        private WebSocketCloseStatus? transportStatus;

        public Session Session => session;

        /// <summary>Runs both loops until the connection ends; returns the close status the server sent, if any.</summary>
        public async Task<WebSocketCloseStatus?> RunAsync(CancellationToken stopping)
        {
            using var onStopping = stopping.Register(() => Close(WebSocketCloseStatus.EndpointUnavailable));
            session.Start();
            var receiving = ReceiveLoopAsync();
            var sent = await SendLoopAsync();

            // The close frame is out (or the socket is gone): give the client a moment to answer it.
            try
            {
                await receiving.WaitAsync(CloseTimeout, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                socket.Abort();
                await receiving;
            }

            return sent;
        }

        private void Close(WebSocketCloseStatus status)
        {
            transportStatus ??= status;
            session.Close();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new ArrayBufferWriter<byte>(ReceiveChunk);
            var discarding = false;
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer.GetMemory(ReceiveChunk), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        session.Close();
                        return;
                    }

                    // After the session closed, what still arrives is read and dropped until the client's close frame.
                    if (session.IsClosed || discarding)
                    {
                        discarding = !result.EndOfMessage;
                        continue;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        Close(WebSocketCloseStatus.InvalidMessageType);
                        discarding = !result.EndOfMessage;
                        continue;
                    }

                    buffer.Advance(result.Count);
                    if (buffer.WrittenCount > maxMessageBytes)
                    {
                        Close(WebSocketCloseStatus.MessageTooBig);
                        buffer.ResetWrittenCount();
                        discarding = !result.EndOfMessage;
                        continue;
                    }

                    if (result.EndOfMessage)
                    {
                        session.Receive(buffer.WrittenSpan.ToArray());
                        buffer.ResetWrittenCount();
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
            {
                // The client vanished, or the server aborted the socket after its close frame went unanswered.
                session.Close();
            }
        }

        private async Task<WebSocketCloseStatus?> SendLoopAsync()
        {
            try
            {
                while (await session.Outgoing.WaitToReadAsync())
                {
                    // A closed session's queue is dropped: the client resyncs when it reconnects.
                    while (!session.IsClosed && session.Outgoing.TryRead(out var message))
                    {
                        await socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);
                    }

                    if (session.IsClosed)
                    {
                        break;
                    }
                }

                if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                {
                    return null;
                }

                var status = transportStatus ?? session.CloseReason switch
                {
                    SessionCloseReason.ProtocolError => WebSocketCloseStatus.ProtocolError,
                    SessionCloseReason.QueueFull => TryAgainLater,
                    _ => WebSocketCloseStatus.NormalClosure,
                };
                await socket.CloseOutputAsync(status, null, CancellationToken.None);
                return status;
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
            {
                session.Close();
                return null;
            }
        }
    }
}
