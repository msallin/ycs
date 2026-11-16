namespace Ycs.Server.Rooms;

/// <summary>The outer message types of y-websocket: the first varuint of every message.</summary>
internal static class MessageType
{
    public const uint Sync = 0;
    public const uint Awareness = 1;
    public const uint QueryAwareness = 3;
}
