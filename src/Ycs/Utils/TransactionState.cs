using System.Collections;

namespace Ycs;

/// <summary>
/// The state vector of a document at a point of a transaction (<see cref="Transaction.BeforeState"/>,
/// <see cref="Transaction.AfterState"/>), read from the store's change log instead of copied: a client is as long as it was
/// when the log had <see cref="Position"/> entries. Empty while the position is -1, before the point is reached and once the
/// transaction ended.
/// </summary>
internal sealed class TransactionState : IReadOnlyDictionary<long, long>
{
    private StructStore _store = null!; // Set with the position.

    internal int Position { get; private set; } = -1;

    internal void Set(StructStore store, int position)
    {
        _store = store;
        Position = position;
    }

    internal void Reset() => Position = -1;

    public int Count => Position < 0 ? 0 : _store.Clients.Count - _store.NewClientsFrom(Position);

    public IEnumerable<long> Keys
    {
        get
        {
            foreach (var (key, _) in this)
            {
                yield return key;
            }
        }
    }

    public IEnumerable<long> Values
    {
        get
        {
            foreach (var (_, value) in this)
            {
                yield return value;
            }
        }
    }

    public long this[long key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The state has no client {key}.");

    public bool ContainsKey(long key) => TryGetValue(key, out _);

    public bool TryGetValue(long key, out long value)
    {
        if (Position >= 0 && _store.Clients.TryGetValue(key, out var structs))
        {
            value = _store.EndAt(structs, Position);
            return value > 0;
        }

        value = 0;
        return false;
    }

    public IEnumerator<KeyValuePair<long, long>> GetEnumerator()
    {
        if (Position < 0)
        {
            yield break;
        }

        // A copy: a transaction that user code starts while it enumerates may add a client, which the state lacks.
        foreach (var (client, structs) in _store.Clients.ToArray())
        {
            var end = _store.EndAt(structs, Position);
            if (end > 0)
            {
                yield return new KeyValuePair<long, long>(client, end);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
