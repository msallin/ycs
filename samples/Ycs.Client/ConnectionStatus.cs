namespace Ycs.Client;

/// <summary>The states of a provider's connection, as y-websocket's <c>status</c> event names them.</summary>
internal enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
}
