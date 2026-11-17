namespace Ycs.Server.Rooms;

/// <summary>The outer message types of y-websocket: the first varuint of every message. Those Ycs frames are its; the server adds the one it ignores.</summary>
internal static class MessageType
{
    public const uint Sync = SyncProtocol.MessageSync;
    public const uint Awareness = SyncProtocol.MessageAwareness;
    public const uint QueryAwareness = 3;
}
