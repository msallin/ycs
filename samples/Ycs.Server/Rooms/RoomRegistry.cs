using System.Collections.Concurrent;

namespace Ycs.Server.Rooms;

/// <summary>
/// The rooms by name. A room lives as long as the process: there is no persistence and no eviction.
/// </summary>
internal sealed class RoomRegistry
{
    private readonly ConcurrentDictionary<string, Room> rooms = new(StringComparer.Ordinal);

    public Room GetOrCreate(string name) => rooms.GetOrAdd(name, static n => new Room(n));
}
