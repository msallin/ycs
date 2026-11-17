namespace Ycs;

/// <summary>The clients of a document by index, so that a slot names its client in four bytes. An index is never reused.</summary>
internal sealed class ClientTable
{
    // Up to this many clients are found by scanning the ids; the dictionary is made when one more comes.
    private const int ScanLimit = 8;

    private long[] _ids = [];
    private int _count;

    // Null while the table holds at most ScanLimit clients; then it holds every one.
    private Dictionary<long, int>? _indexes;

    // The last client asked for (one client types most of the time); _lastIndex is -1 before the first.
    private long _lastClient;
    private int _lastIndex = -1;

    public int Count => _count;

    public long this[int index] => (uint)index < (uint)_count ? _ids[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>The index of <paramref name="client"/>, which gets the next one when new.</summary>
    public int GetOrAdd(long client)
    {
        if (_lastIndex >= 0 && client == _lastClient)
        {
            return _lastIndex;
        }

        if (!TryGetIndex(client, out var index))
        {
            index = _count;
            if (index == _ids.Length)
            {
                Array.Resize(ref _ids, Math.Max(1, index * 2));
            }

            _ids[index] = client;
            _count++;
            if (_indexes != null)
            {
                _indexes.Add(client, index);
            }
            else if (_count > ScanLimit)
            {
                _indexes = new Dictionary<long, int>(_count * 2);
                for (var i = 0; i < _count; i++)
                {
                    _indexes.Add(_ids[i], i);
                }
            }
        }

        _lastClient = client;
        _lastIndex = index;
        return index;
    }

    public bool TryGetIndex(long client, out int index)
    {
        if (_indexes != null)
        {
            return _indexes.TryGetValue(client, out index);
        }

        var ids = _ids;
        for (var i = 0; i < _count; i++)
        {
            if (ids[i] == client)
            {
                index = i;
                return true;
            }
        }

        index = 0;
        return false;
    }
}
