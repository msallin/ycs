using System.Collections.Concurrent;

namespace Ycs.Server.Rooms;

/// <summary>
/// The rooms by name. A room lives as long as the process: there is no persistence and no eviction.
/// </summary>
internal sealed class RoomRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Room> rooms = new(StringComparer.Ordinal);

    public Room GetOrCreate(string name) => rooms.GetOrAdd(name, static (n, t) => new Room(n, t), time);

    /// <summary>A moment's view of the rooms; a room created meanwhile may be missing.</summary>
    public IEnumerable<Room> Rooms => rooms.Values;
}
