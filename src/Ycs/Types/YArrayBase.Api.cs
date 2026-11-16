// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public abstract partial class YArrayBase
{
    private protected YArrayBase(bool useSearchMarkers = true)
    {
        _searchMarkers = useSearchMarkers ? new ArraySearchMarkerCollection() : null;
    }

    public IEnumerator<object> GetEnumerator()
    {
        return EnumerateContent().GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return EnumerateContent().GetEnumerator();
    }

    private protected void InsertGenerics(Transaction transaction, int index, ICollection<object> content)
    {
        if (index > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Length exceeded: the index is beyond the length ({Length}). Nothing was inserted.");
        }

        if (index == 0)
        {
            if (_searchMarkers?.Count > 0)
            {
                _searchMarkers.UpdateMarkerChanges(transaction, index, content.Count);
            }

            InsertGenericsAfterReturningLast(transaction, default, content);
            return;
        }

        int startIndex = index;
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;

            // We need to iterate one to the left so that the algorithm works.
            if (index == 0)
            {
                // @todo: refactor this as it actually doesn't consider formats.
                n = n.Prev.AsItem;
                index += !n.IsNull && n.Countable && !n.Deleted ? n.Length : 0;
            }
        }

        var steps = 0;
        for (; !n.IsNull; n = n.Right.AsItem)
        {
            if (++steps == transaction.Doc.LiteralWalkStepsForTests)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index <= n.Length)
                {
                    if (index < n.Length)
                    {
                        // Insert in-between. After a stale marker beyond the index, index is negative, as in Yjs: the
                        // clock lies in an earlier struct of n's client, which is split as Yjs splits it, or below 0,
                        // where the lookup throws as Yjs' findIndexSS does.
                        transaction.Doc.Store.GetItemCleanStart(transaction, n.Id.Client, n.Id.Clock + index);
                    }

                    break;
                }

                index -= n.Length;
            }
        }

        if (_searchMarkers?.Count > 0)
        {
            var right = n.IsNull ? default : n.Right;
            _searchMarkers.UpdateMarkerChanges(transaction, startIndex, content.Count);
            n = LeftOf(n, right);
        }

        InsertGenericsAfterReturningLast(transaction, n, content);
    }

    private protected void Delete(Transaction transaction, int index, int length)
    {
        if (length == 0)
        {
            return;
        }

        int startIndex = index;
        int startLength = length;
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }

        // Compute the first item to be deleted.
        var steps = 0;
        for (; !n.IsNull && index > 0; n = n.Right.AsItem)
        {
            if (++steps == transaction.Doc.LiteralWalkStepsForTests)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + index));
                }

                index -= n.Length;
            }
        }

        // Delete all items until done.
        while (length > 0 && !n.IsNull)
        {
            if (!n.Deleted)
            {
                if (length < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + length));
                }

                n.Delete(transaction);
                length -= n.Length;
            }

            n = n.Right.AsItem;
        }

        if (length > 0)
        {
            throw new Exception("Array length exceeded");
        }

        if (_searchMarkers?.Count > 0)
        {
            _searchMarkers.UpdateMarkerChanges(transaction, startIndex, -startLength + length /* in case we remove the above exception */);
        }
    }

    /// <summary>
    /// Yjs' <c>typeListSlice</c>: never throws. A negative index counts from the end; an empty range
    /// gives an empty list; a start that is still negative then yields null for each index before 0,
    /// as JavaScript reads them.
    /// </summary>
    private protected IReadOnlyList<object> InternalSlice(int start, int end)
    {
        if (start < 0)
        {
            start += Length;
        }

        if (end < 0)
        {
            end += Length;
        }

        int length = end - start;
        var cs = new List<object>();
        var n = _start;

        while (!n.IsNull && length > 0)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                if (c.Count <= start)
                {
                    start -= c.Count;
                }
                else
                {
                    for (int i = start; i < c.Count && length > 0; i++)
                    {
                        cs.Add(i < 0 ? null : c[i]);
                        length--;
                    }

                    start = 0;
                }
            }

            n = n.Right.AsItem;
        }

        return cs.AsReadOnly();
    }

    private protected void ForEach(Action<object, int, YArrayBase> fun)
    {
        int index = 0;
        var n = _start;

        while (!n.IsNull)
        {
            var id = n.Id;
            var departures = Doc.Store.Departures;
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                foreach (var cItem in c)
                {
                    fun(cItem, index++, this);
                }
            }

            n = Doc.Store.NextAfter(n, id, departures);
        }
    }

    private protected void ForEachSnapshot(Action<object, int, YArrayBase> fun, Snapshot snapshot)
    {
        // Visibility is decided per item, by its first clock: Yjs' items.
        Doc?.MaterializeRunExtensions();
        int index = 0;
        var n = _start;

        while (!n.IsNull)
        {
            if (n.Countable && n.IsVisible(snapshot))
            {
                var c = n.Content.GetContent();
                foreach (var value in c)
                {
                    fun(value, index++, this);
                }
            }

            n = n.Right.AsItem;
        }
    }

    private protected IEnumerable<object> EnumerateContent()
    {
        var n = _start;
        while (!n.IsNull)
        {
            while (!n.IsNull && n.Deleted)
            {
                n = n.Right.AsItem;
            }

            // Check if we reached the end, no need to check currentContent, because it does not exist.
            if (n.IsNull)
            {
                yield break;
            }

            var id = n.Id;
            var departures = Doc.Store.Departures;
            var currentContent = n.Content.GetContent();
            foreach (var c in currentContent)
            {
                yield return c;
            }

            // We used content of n, now iterate to next.
            n = Doc.Store.NextAfter(n, id, departures);
        }
    }
}
