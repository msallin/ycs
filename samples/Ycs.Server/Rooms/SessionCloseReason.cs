namespace Ycs.Server.Rooms;

internal enum SessionCloseReason
{
    /// <summary>The session is open.</summary>
    None,

    /// <summary>The transport closed it: the client went away or the server shuts down.</summary>
    Disconnected,

    /// <summary>The client sent a message that is empty, of an unknown type, malformed, or that the document rejected.</summary>
    ProtocolError,

    /// <summary>The client read its messages too slowly; it resyncs when it reconnects.</summary>
    QueueFull,
}
