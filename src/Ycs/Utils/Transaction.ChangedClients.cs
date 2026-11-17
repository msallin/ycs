namespace Ycs;

/// <summary>A client whose list grew in a transaction: its end then, before the transaction and at the start of its cleanup.</summary>
internal readonly record struct ClientChange(long Client, ClientStructs Structs, long Before, long After);

/// <summary>
/// The clients <see cref="Transaction.AfterState"/> has longer than <see cref="Transaction.BeforeState"/>, each once, in the
/// order of the store's clients (as Yjs walks its state vector, and what the order of deletes and redone items follows);
/// the work of a transaction is that of its changes, not of the document's clients.
/// </summary>
internal readonly struct ChangedClients(StructStore store, int from, int to)
{
    public Enumerator GetEnumerator() => new(store, from, to);

    internal struct Enumerator : IDisposable
    {
        private readonly StructStore _store;
        private readonly int _from;
        private readonly int _to;

        // Positions in the log, as (ordinal << 32 | position) in the order of the clients; null when the log order
        // is that order already (one client changed, or they changed in the order they were added).
        private long[]? _sorted;
        private int _count;
        private int _next;

        public Enumerator(StructStore store, int from, int to)
        {
            _store = store;
            _from = from;
            _to = to;
            _next = from - 1;
            var inOrder = true;
            var lastOrdinal = -1;
            var count = 0;
            for (var i = from; i < to; i++)
            {
                if (store.IsFirstFrom(i, from))
                {
                    var ordinal = store.ChangeAt(i).Structs.Ordinal;
                    inOrder &= ordinal > lastOrdinal;
                    lastOrdinal = ordinal;
                    count++;
                }
            }

            if (!inOrder)
            {
                _sorted = BufferPool<long>.Rent(count);
                var n = 0;
                for (var i = from; i < to; i++)
                {
                    if (store.IsFirstFrom(i, from))
                    {
                        _sorted[n++] = ((long)store.ChangeAt(i).Structs.Ordinal << 32) | (uint)i;
                    }
                }

                _sorted.AsSpan(0, count).Sort();
                _count = count;
                _next = -1;
            }
        }

        public ClientChange Current { get; private set; }

        public bool MoveNext()
        {
            int position;
            if (_sorted != null)
            {
                if (++_next >= _count)
                {
                    return false;
                }

                position = (int)(_sorted[_next] & 0xFFFFFFFF);
            }
            else
            {
                do
                {
                    if (++_next >= _to)
                    {
                        return false;
                    }
                }
                while (!_store.IsFirstFrom(_next, _from));

                position = _next;
            }

            var change = _store.ChangeAt(position);
            Current = new ClientChange(change.Client, change.Structs, change.EndBefore, _store.EndAt(change.Structs, _to));
            return true;
        }

        public void Dispose()
        {
            if (_sorted != null)
            {
                BufferPool<long>.Return(_sorted);
                _sorted = null;
            }
        }
    }
}
