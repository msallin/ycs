namespace Ycs.Server.Transports;

/// <summary>Why the server aborted a SignalR connection, if it did.</summary>
internal enum SyncHubCloseReason
{
    /// <summary>The server did not abort it.</summary>
    None,

    /// <summary>The client sent a null message or one over the size limit.</summary>
    MessageRejected,

    /// <summary>The session closed for a protocol error or a full queue.</summary>
    SessionClosed,
}
