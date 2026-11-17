namespace Ycs;

/// <summary>The clients of a document by index, so that a slot names its client in four bytes. An index is never reused.</summary>
internal sealed class ClientTable
{
    private readonly List<long> _ids = [];
    private readonly Dictionary<long, int> _indexes = [];

    // The last client asked for (one client types most of the time); _lastIndex is -1 before the first.
    private long _lastClient;
    private int _lastIndex = -1;

    public int Count => _ids.Count;

    public long this[int index] => _ids[index];

    /// <summary>The index of <paramref name="client"/>, which gets the next one when new.</summary>
    public int GetOrAdd(long client)
    {
        if (_lastIndex >= 0 && client == _lastClient)
        {
            return _lastIndex;
        }

        if (!_indexes.TryGetValue(client, out var index))
        {
            index = _ids.Count;
            _ids.Add(client);
            _indexes.Add(client, index);
        }

        _lastClient = client;
        _lastIndex = index;
        return index;
    }

    public bool TryGetIndex(long client, out int index) => _indexes.TryGetValue(client, out index);
}
