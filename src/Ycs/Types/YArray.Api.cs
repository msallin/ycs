// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public sealed partial class YArray
{
    public YArray()
        : this(null)
    {
        // Do nothing.
    }

    public YArray(IEnumerable<object> prelimContent = null)
    {
        _prelimContent = prelimContent != null ? new List<object>(prelimContent.Select(ValueFreezer.Detach)) : new List<object>();
    }

    /// <summary>
    /// Construct a new YArray containing the specified items. Mirrors Yjs' static <c>Y.Array.from(items)</c>.
    /// </summary>
    public static YArray From(IEnumerable<object> items)
    {
        var a = new YArray();
        a.Push(new List<object>(items ?? Array.Empty<object>()));
        return a;
    }

    public override int Length => _prelimContent?.Count ?? base.Length;

    /// <summary>
    /// Returns the i-th element from this YArray. C# idiom: indexer form of <see cref="Get"/>.
    /// There is intentionally no setter — Yjs arrays have no in-place index assignment; use
    /// <see cref="Delete(int, int)"/> + <see cref="Insert"/> (or <see cref="Add"/>) instead.
    /// </summary>
    public object this[int index] => Get(index);

    public YArray Clone() => InternalClone() as YArray;

    /// <summary>
    /// Inserts new content at an index.
    /// </summary>
    public void Insert(int index, ICollection<object> content)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, content), static (s, tr) => s.self.InsertGenerics(tr, s.index, s.content));
        }
        else
        {
            PrelimInsert(_prelimContent, index, content);
        }
    }

    /// <summary>
    /// Appends content after the last item, deleted ones included, as Yjs' <c>push</c> (which is not
    /// an insert at <see cref="Length"/>: the bytes and the place among concurrent appends differ).
    /// </summary>
    public void Add(ICollection<object> content)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, content), static (s, tr) => s.self.PushGenerics(tr, s.content));
        }
        else
        {
            _prelimContent.AddRange(content.Select(ValueFreezer.Detach));
        }
    }

    /// <summary>Yjs' <c>push(content)</c>; the same as <see cref="Add"/>.</summary>
    public void Push(ICollection<object> content) => Add(content);

    public void Unshift(ICollection<object> content)
    {
        Insert(0, content);
    }

    public void Delete(int index, int length = 1)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, length), static (s, tr) => s.self.Delete(tr, s.index, s.length));
        }
        else
        {
            PrelimDelete(_prelimContent, index, length);
        }
    }

    public IReadOnlyList<object> Slice(int start = 0) => InternalSlice(start, Length);

    public IReadOnlyList<object> Slice(int start, int end) => InternalSlice(start, end);

    public object Get(int index)
    {
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }

        if (index < 0)
        {
            // Yjs' typeListGet reads the first visible item's content at the negative offset, which is undefined: for a
            // negative index, and for a marker beyond the index (an observer of another type reads while a remote
            // transaction's cleanup has not yet emptied this list's stale markers).
            return default;
        }

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
                    return ValueAt(n.Content, index);
                }

                index -= n.Length;
            }
        }

        return default;
    }

    public IList<object> ToArray()
    {
        var cs = new List<object>();
        cs.AddRange(EnumerateList());
        return cs;
    }

    /// <summary>
    /// Returns a list with the result of calling a provided function on every element of this
    /// YArray. Mirrors Yjs' <c>map(f)</c>.
    /// </summary>
    public List<TResult> Map<TResult>(Func<object, int, YArray, TResult> f)
    {
        var result = new List<TResult>();
        int index = 0;

        foreach (var item in EnumerateList())
        {
            result.Add(f(item, index++, this));
        }

        return result;
    }

    /// <summary>
    /// Executes a provided function once on every element of this YArray. Mirrors Yjs' <c>forEach(f)</c>.
    /// </summary>
    public void ForEach(Action<object, int, YArray> f)
    {
        int index = 0;

        foreach (var item in EnumerateList())
        {
            f(item, index++, this);
        }
    }

    /// <summary>
    /// Transforms this Shared Type to a JSON-compatible list. Mirrors Yjs' <c>toJSON()</c>
    /// (<c>this.map(c =&gt; c instanceof YType ? c.toJSON() : c)</c>).
    /// </summary>
    public override object ToJson() => JsonTree(this);
}
