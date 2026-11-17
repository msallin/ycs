namespace Ycs;

/// <summary>
/// The reservations of an arena: a run's end offset to the end of the room kept after it. A mutable struct kept in a
/// field that is not readonly and never copied: the first entry lives in the struct and the dictionary is made for the
/// second, so a document with one growing run holds no dictionary. Reserved ends are positive (an end is past an offset
/// and a reserved end past an end).
/// </summary>
internal struct TailTable
{
    private long _end;
    private long _reservedEnd;
    private Dictionary<long, long>? _more;

    public readonly int Count => (_reservedEnd != 0 ? 1 : 0) + (_more?.Count ?? 0);

    public readonly bool TryGetValue(long end, out long reservedEnd)
    {
        if (_reservedEnd != 0 && _end == end)
        {
            reservedEnd = _reservedEnd;
            return true;
        }

        reservedEnd = 0;
        return _more != null && _more.TryGetValue(end, out reservedEnd);
    }

    public void Set(long end, long reservedEnd)
    {
        if (_reservedEnd == 0 || _end == end)
        {
            _end = end;
            _reservedEnd = reservedEnd;
        }
        else
        {
            (_more ??= [])[end] = reservedEnd;
        }
    }

    public bool Remove(long end, out long reservedEnd)
    {
        if (_reservedEnd != 0 && _end == end)
        {
            reservedEnd = _reservedEnd;
            _reservedEnd = 0;
            return true;
        }

        reservedEnd = 0;
        return _more != null && _more.Remove(end, out reservedEnd);
    }

    public bool Remove(long end) => Remove(end, out _);

    public void Clear()
    {
        _reservedEnd = 0;
        _more?.Clear();
    }

    public readonly IEnumerable<(long End, long ReservedEnd)> Entries()
    {
        var snapshot = new List<(long, long)>(Count);
        if (_reservedEnd != 0)
        {
            snapshot.Add((_end, _reservedEnd));
        }

        if (_more != null)
        {
            foreach (var (end, reservedEnd) in _more)
            {
                snapshot.Add((end, reservedEnd));
            }
        }

        return snapshot;
    }
}
