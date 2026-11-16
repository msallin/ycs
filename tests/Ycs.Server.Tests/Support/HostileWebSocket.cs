using System.Net.WebSockets;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// A client that stopped reading: every send waits until the socket is aborted, and so does every receive.
/// </summary>
internal sealed class HostileWebSocket : WebSocket
{
    private readonly TaskCompletionSource aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int sends;

    public bool Aborted => aborted.Task.IsCompleted;

    /// <summary>How many sends began, the one that waits included.</summary>
    public int Sends => Volatile.Read(ref sends);

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;

    public override string? SubProtocol => null;

    public override void Abort() => aborted.TrySetResult();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Wait();

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Wait();

    public override void Dispose() => Abort();

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await Wait();
        throw new InvalidOperationException("unreachable");
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref sends);
        return Wait();
    }

    private async Task Wait()
    {
        await aborted.Task;
        throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
    }
}
