// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Diagnostics;

namespace Ycs;

public abstract partial class YArrayBase
{
    private protected YArrayBase(bool useSearchMarkers = true)
    {
        _searchMarkers = useSearchMarkers ? new ArraySearchMarkerCollection() : null;
    }

    private protected void InsertGenerics(Transaction transaction, int index, ICollection<object?> content)
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
            throw new InvalidOperationException("Array length exceeded");
        }

        if (_searchMarkers?.Count > 0)
        {
            _searchMarkers.UpdateMarkerChanges(transaction, startIndex, -startLength + length /* in case we remove the above exception */);
        }
    }

    /// <summary>The countable content from <paramref name="start"/> to <paramref name="end"/> (exclusive), as stored; the caller has checked the range.</summary>
    private protected List<object?> InternalSlice(int start, int end)
    {
        var length = end - start;
        var cs = new List<object?>(length);
        var n = _start;

        while (!n.IsNull && length > 0)
        {
            if (n.Countable && !n.Deleted)
            {
                if (n.Length <= start)
                {
                    start -= n.Length;
                }
                else
                {
                    for (var i = start; i < n.Length && length > 0; i++)
                    {
                        cs.Add(ValueAt(n.Content, i));
                        length--;
                    }

                    start = 0;
                }
            }

            n = n.Right.AsItem;
        }

        return cs;
    }

    /// <summary>The value at <paramref name="index"/> of the document's list, as stored; false when no countable content is there.</summary>
    private protected bool TryGetRawAt(int index, out object? value)
    {
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }

        value = null;
        if (index < 0)
        {
            // Yjs' typeListGet reads the first visible item's content at the negative offset, which is undefined: for a
            // negative index, and for a marker beyond the index (an observer of another type reads while a remote
            // transaction's cleanup has not yet emptied this list's stale markers).
            return false;
        }

        Debug.Assert(Doc != null);
        var steps = 0;
        for (; !n.IsNull; n = n.Right.AsItem)
        {
            if (++steps == Doc.LiteralWalkStepsForTests)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    value = ValueAt(n.Content, index);
                    return true;
                }

                index -= n.Length;
            }
        }

        return false;
    }

    /// <summary>The countable content visible in <paramref name="snapshot"/> (Yjs' <c>typeListToArraySnapshot</c>), as stored.</summary>
    private protected List<object?> ContentAtSnapshot(Snapshot snapshot)
    {
        // Visibility is decided per item, by its first clock: Yjs' items.
        Doc?.MaterializeRunExtensions();
        var result = new List<object?>();
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (n.Countable && n.IsVisible(snapshot))
            {
                for (var i = 0; i < n.Length; i++)
                {
                    result.Add(ValueAt(n.Content, i));
                }
            }
        }

        return result;
    }

    /// <summary>The countable content of the document's list, as stored, in order; it survives the structs merging or leaving while the caller holds it.</summary>
    private protected IEnumerable<object?> EnumerateRaw()
    {
        Debug.Assert(Doc != null);
        var n = _start;

        while (!n.IsNull)
        {
            var id = n.Id;
            var departures = Doc.Store.Departures;
            if (n.Countable && !n.Deleted)
            {
                var length = n.Length;
                var content = n.Content;
                for (var i = 0; i < length; i++)
                {
                    yield return ValueAt(content, i);
                }
            }

            n = Doc.Store.NextAfter(n, id, departures);
        }
    }
}
