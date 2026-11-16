#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// Structs that could not be integrated yet (Yjs' <c>store.pendingStructs</c>), and per client the
    /// lowest clock that is missing for them. Yjs keeps one encoded update and merges every new remainder
    /// into it (<c>mergeUpdatesV2([pending.update, rest])</c>), which costs the size of everything pending per
    /// update; here a remainder that shares no clock with what waits is kept as it comes (an encoded v2 update),
    /// and the remainders are merged once, when they are retried or compacted.
    /// <br/>
    /// A remainder that shares a clock with what waits is merged at once, as Yjs merges every one: where structs
    /// overlap, merging is not associative. Remainders disjoint by clock give the fold's bytes in one merge
    /// (docs/api.md, "Pending structs, kept").
    /// <br/>
    /// They are re-read from their encoding when they are retried, so a root type realized in between
    /// (e.g. with <see cref="YDoc.GetText"/>) is resolved then.
    /// </summary>
    internal sealed class PendingStructs
    {
        // The clocks of the GC structs and items of every remainder added (Skip structs are gaps). Never lowered: a
        // remainder rejected after Add stays recorded, which can only make a later Add merge where it need not,
        // and a merge at once is Yjs' own step.
        private readonly ClockRanges _clocks = new();

        public Dictionary<long, long> Missing { get; } = new();
        public List<byte[]> Updates { get; } = new();
        public long Bytes { get; private set; }

        /// <summary>How many structs <see cref="Updates"/> hold (Skip structs included), as they were written.</summary>
        public long Structs { get; private set; }

        /// <summary>Adds the remainder <paramref name="update"/>, which holds the structs of <paramref name="rest"/>.</summary>
        public void Add(byte[] update, long structs, StructStore rest)
        {
            if (SharesAClock(rest))
            {
                // Yjs' pending.update = mergeUpdatesV2([pending.update, rest.update]).
                var merged = new List<byte[]> { Merge(), update };
                var count = Structs + structs;
                Replace(UpdateOperations.MergeUpdatesTrusted(merged, ref count), count);
            }
            else
            {
                Updates.Add(update);
                Bytes += update.Length;
                Structs += structs;
            }

            foreach (var (client, list) in rest.Clients)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var str = list[i].ToStruct();
                    if (str is not Skip)
                    {
                        _clocks.Add(client, str.Id.Clock, str.Id.Clock + str.Length);
                    }
                }
            }
        }

        private bool SharesAClock(StructStore rest)
        {
            foreach (var (client, list) in rest.Clients)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var str = list[i].ToStruct();
                    if (str is not Skip && _clocks.Overlaps(client, str.Id.Clock, str.Id.Clock + str.Length))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public byte[] Merge()
        {
            var structs = Structs;
            var merged = UpdateOperations.MergeUpdatesTrusted(Updates, ref structs);
            Replace(merged, structs);
            return merged;
        }

        /// <summary>Makes <paramref name="update"/> all that waits.</summary>
        public void Replace(byte[] update, long structs)
        {
            Updates.Clear();
            Updates.Add(update);
            Bytes = update.Length;
            Structs = structs;
        }
    }

    internal PendingStructs Pending { get; set; }

    /// <summary>Deletions of structs this store does not have yet (Yjs' <c>store.pendingDs</c>).</summary>
    internal PendingDeletes PendingDs { get; } = new();

    internal bool HasPending => Pending != null || PendingDs.Count != 0;

    /// <summary>The encoded size of what is pending, as <see cref="UpdateLimits.MaxPendingBytes"/> counts it.</summary>
    internal long PendingBytes => (Pending?.Bytes ?? 0) + PendingDs.Bytes;

    /// <summary>The structs pending, as <see cref="UpdateLimits.MaxPendingStructs"/> counts them.</summary>
    internal long PendingStructCount => Pending?.Structs ?? 0;

    /// <summary>
    /// Rejects an update that would make more wait than <paramref name="limits"/> allow. One that lets
    /// pending structs integrate (<paramref name="retry"/>) may add up to the limits on top of what
    /// waits, since what waits is then integrated, wholly or in part: so what waits never exceeds
    /// twice the limits, and the retry's re-read is bounded with it. Returns true when
    /// <paramref name="rest"/> had to be merged into <see cref="Pending"/> to fit (a duplicate).
    /// </summary>
    private bool CheckPendingLimits(UpdateLimits limits, bool retry, byte[] rest, long restStructs, StructStore restStore, long deleteGrowth)
    {
        var restBytes = rest?.Length ?? 0;
        if (restStructs == 0 && restBytes + deleteGrowth <= 0)
        {
            return false;
        }

        var maxStructs = retry ? Twice(limits.MaxPendingStructs) : limits.MaxPendingStructs;
        var maxBytes = retry ? Twice(limits.MaxPendingBytes) : limits.MaxPendingBytes;
        if (Fits(restStructs, restBytes))
        {
            return false;
        }

        // Duplicates (the same update received again) shrink when merged, also with the update's rest.
        Pending?.Merge();
        PendingDs.Compact();
        if (Fits(restStructs, restBytes))
        {
            return false;
        }

        if (rest != null && Pending != null)
        {
            var (pending, pendingStructs) = (Pending.Updates[0], Pending.Structs);
            Pending.Add(rest, restStructs, restStore);
            Pending.Merge();
            if (Fits(0, 0))
            {
                return true;
            }

            // The remainder's clocks stay recorded: that can only make a later Add merge where it need not.
            Pending.Replace(pending, pendingStructs);
        }

        throw new YUpdateException(
            $"The update depends on structs this document does not have, and keeping it until they arrive would grow " +
            $"the pending data beyond {maxStructs} structs or {maxBytes} bytes (UpdateLimits.MaxPendingStructs, " +
            $"UpdateLimits.MaxPendingBytes). The document is unchanged.");

        bool Fits(long addedStructs, long addedBytes) =>
            addedStructs <= maxStructs - PendingStructCount && addedBytes + deleteGrowth <= maxBytes - PendingBytes;

        static long Twice(long limit) => limit > long.MaxValue / 2 ? long.MaxValue : 2 * limit;
    }

    /// <summary>
    /// Deletions of structs the store does not have yet (Yjs' <c>store.pendingDs</c>). Yjs keeps them
    /// as one encoded update and re-reads all of it on every update; here they are kept per client
    /// and a client's ranges are only looked at once its state reaches the lowest of them.
    /// </summary>
    internal sealed class PendingDeletes
    {
        private sealed class Entry
        {
            public List<DeleteSet.DeleteItem> Ranges = new();
            public long MinClock = long.MaxValue;
            public long Bytes;
        }

        private readonly Dictionary<long, Entry> _clients = new();

        /// <summary>The size of the ranges as a v1 delete set would encode them.</summary>
        public long Bytes { get; private set; }

        /// <summary>How many ranges were ever added (it only grows), to tell whether an operation added one.</summary>
        public long Additions { get; private set; }

        public int Count => _clients.Count;

        public void Add(long client, long clock, long length)
        {
            if (!_clients.TryGetValue(client, out var entry))
            {
                entry = new Entry { Bytes = HeaderSize(client) };
                _clients[client] = entry;
                Bytes += entry.Bytes;
            }

            entry.Ranges.Add(new DeleteSet.DeleteItem(clock, length));
            Additions++;
            entry.MinClock = Math.Min(entry.MinClock, clock);
            var size = RangeSize(clock, length);
            entry.Bytes += size;
            Bytes += size;
        }

        /// <summary>By how much <see cref="Bytes"/> would grow if the parts of <paramref name="ds"/> beyond <paramref name="getState"/> were added.</summary>
        public long Growth(DeleteSet ds, IntegrationPlan plan)
        {
            long growth = 0;
            foreach (var (client, deletes) in ds.Clients)
            {
                var state = plan.GetState(client);
                var added = false;
                foreach (var delete in deletes)
                {
                    var clockEnd = delete.Clock + delete.Length;
                    if (clockEnd > state)
                    {
                        var clock = Math.Max(delete.Clock, state);
                        growth += RangeSize(clock, clockEnd - clock);
                        added = true;
                    }
                }

                if (added && !_clients.ContainsKey(client))
                {
                    growth += HeaderSize(client);
                }
            }

            return growth;
        }

        /// <summary>Sorts and merges every client's ranges, dropping duplicates.</summary>
        public void Compact()
        {
            var ds = new DeleteSet();
            foreach (var (client, entry) in _clients)
            {
                ds.Clients[client] = entry.Ranges;
            }

            ds.SortAndMergeDeleteSet();
            Bytes = 0;
            foreach (var (client, entry) in _clients)
            {
                entry.Bytes = HeaderSize(client);
                foreach (var range in entry.Ranges)
                {
                    entry.Bytes += RangeSize(range.Clock, range.Length);
                }

                Bytes += entry.Bytes;
            }
        }

        /// <summary>A copy of every client's ranges, sorted and merged (the v2 encoding needs them ascending).</summary>
        public DeleteSet ToDeleteSet()
        {
            var ds = new DeleteSet();
            foreach (var (client, entry) in _clients)
            {
                ds.Clients[client] = new List<DeleteSet.DeleteItem>(entry.Ranges);
            }

            ds.SortAndMergeDeleteSet();
            return ds;
        }

        /// <summary>Adds the ranges <see cref="TakeApplicable"/> will take once <paramref name="plan"/> is carried out, as (client, clock, end).</summary>
        public void AddApplicableRanges(IntegrationPlan plan, List<(long Client, long Clock, long End)> ranges)
        {
            foreach (var (client, entry) in _clients)
            {
                if (entry.MinClock < plan.GetState(client))
                {
                    foreach (var range in entry.Ranges)
                    {
                        ranges.Add((client, range.Clock, range.Clock + range.Length));
                    }
                }
            }
        }

        /// <summary>Removes and returns the ranges of the clients whose state reached one of them, or null.</summary>
        public DeleteSet TakeApplicable(StructStore store)
        {
            List<long> applicable = null;
            foreach (var (client, entry) in _clients)
            {
                if (entry.MinClock < store.GetState(client))
                {
                    (applicable ??= new List<long>()).Add(client);
                }
            }

            if (applicable == null)
            {
                return null;
            }

            // In the order Yjs reads its pending deletions back (writeDeleteSet writes the clients from the highest id
            // down): it decides the order of the transaction's delete set, and so of the garbage collection filter's calls.
            applicable.Sort(static (a, b) => b.CompareTo(a));
            var ds = new DeleteSet();
            foreach (var client in applicable)
            {
                ds.Clients[client] = _clients[client].Ranges;
                Bytes -= _clients[client].Bytes;
                _clients.Remove(client);
            }

            return ds;
        }

        private static long HeaderSize(long client) => VarUintSize(client) + 1;

        private static long RangeSize(long clock, long length) => VarUintSize(clock) + VarUintSize(length);

        private static int VarUintSize(long value) => value < 1L << 7 ? 1 : value < 1L << 14 ? 2 : value < 1L << 21 ? 3 : value < 1L << 28 ? 4 : 5;
    }
}
