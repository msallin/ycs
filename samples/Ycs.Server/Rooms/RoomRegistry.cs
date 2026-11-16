using System.Collections.Concurrent;

namespace Ycs.Server.Rooms;

/// <summary>
/// The rooms by name. A room lives as long as the process: there is no persistence and no eviction.
/// </summary>
internal sealed class RoomRegistry(TimeProvider time, ILogger<Room>? logger = null)
{
    private readonly ConcurrentDictionary<string, Room> rooms = new(StringComparer.Ordinal);

    public Room GetOrCreate(string name) => rooms.GetOrAdd(name, static (n, state) => new Room(n, state.time, logger: state.logger), (time, logger));

    /// <summary>A moment's view of the rooms; a room created meanwhile may be missing.</summary>
    public IEnumerable<Room> Rooms => rooms.Values;
}
