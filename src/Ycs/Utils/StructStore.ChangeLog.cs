namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>The end of a client's list before the first change <see cref="ClientStructs.End"/> had since a transaction began.</summary>
    internal readonly record struct Change(ClientStructs Structs, long Client, long EndBefore, int Previous);

    // Where a client's End moved, in the order it moved (AddStruct and NoteRunExtended are the only places): one entry
    // per client and generation, written before the move. A transaction records the log's length when it begins, so
    // that the state before it, the state at the start of its cleanup, and the clients it changed are read from the
    // entries at and after those positions, whatever other transactions changed since. The entries of a client
    // chain through Previous, newest first (ClientStructs.LastChange). The log is emptied when the last batch of
    // cleanups has finished.
    private Change[] _changes = new Change[4];
    private int _changeCount;
    private long _generation = 1;
    private int _batches;

    internal int ChangeCount => _changeCount;

    internal int ChangeCapacity => _changes.Length;

    internal Change ChangeAt(int position) => _changes[position];

    /// <summary>A transaction begins: the position from which its changes are logged.</summary>
    internal int BeginChanges()
    {
        _generation++;
        return _changeCount;
    }

    /// <summary>A batch of cleanups begins; the log is kept until every batch that began has ended.</summary>
    internal void EnterBatch() => _batches++;

    internal void ExitBatch()
    {
        if (--_batches > 0)
        {
            return;
        }

        for (var i = 0; i < _changeCount; i++)
        {
            _changes[i].Structs.LastChange = -1;
        }

        Array.Clear(_changes, 0, _changeCount);
        _changeCount = 0;
        if (_changes.Length > 1024)
        {
            _changes = new Change[4];
        }
    }

    private void LogChange(ClientStructs structs, long client)
    {
        if (structs.LoggedGeneration == _generation)
        {
            return;
        }

        if (_changeCount == _changes.Length)
        {
            Array.Resize(ref _changes, _changeCount * 2);
        }

        structs.LoggedGeneration = _generation;
        _changes[_changeCount] = new Change(structs, client, structs.End, structs.LastChange);
        structs.LastChange = _changeCount++;
    }

    /// <summary>The end of <paramref name="structs"/> when the log had <paramref name="position"/> entries.</summary>
    internal long EndAt(ClientStructs structs, int position)
    {
        var entry = structs.LastChange;
        if (entry < position)
        {
            return structs.End;
        }

        for (var previous = _changes[entry].Previous; previous >= position; previous = _changes[entry].Previous)
        {
            entry = previous;
        }

        return _changes[entry].EndBefore;
    }

    /// <summary>Whether the entry at <paramref name="position"/> is the first of its client at or after <paramref name="from"/>.</summary>
    internal bool IsFirstFrom(int position, int from) => _changes[position].Previous < from;

    /// <summary>How many clients the entries from <paramref name="from"/> on introduce (a client's first entry has no end before it).</summary>
    internal int NewClientsFrom(int from)
    {
        var count = 0;
        for (var i = from; i < _changeCount; i++)
        {
            if (_changes[i].EndBefore == 0)
            {
                count++;
            }
        }

        return count;
    }
}
