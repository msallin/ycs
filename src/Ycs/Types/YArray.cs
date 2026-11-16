// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

public class YArrayEvent : YEvent
{
    internal YArrayEvent(YArray arr, Transaction transaction)
        : base(arr, transaction)
    {
        // Do nothing.
    }
}

public partial class YArray : YArrayBase
{
    public const byte YArrayRefId = 0;

    private List<object> _prelimContent;

    public YArray()
        : this(null)
    {
        // Do nothing.
    }

    public YArray(IEnumerable<object> prelimContent = null)
    {
        _prelimContent = prelimContent != null ? new List<object>(prelimContent) : new List<object>();
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

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        // Bug fix: `_prelimContent` must be cleared *before* flushing it via Insert(), not after.
        // `Length` is overridden as `_prelimContent?.Count ?? base.Length` so it's readable before
        // attach; but Item.Integrate() (Structs/Item.cs) does `parent.Length += item.Length` for
        // every item it integrates -- and that `+=` reads `Parent.Length` through the very same
        // virtual getter. While `_prelimContent` was still non-null during the Insert() below, that
        // read returned the *stale prelim count* instead of the real, incrementally-accumulated
        // `base.Length`, so every integrated item's length was counted once for real and once again
        // via the stale prelim-count fallback -- silently doubling `Length` (content itself was
        // unaffected; only the cached counter was wrong). Nulling first makes the getter fall
        // through to `base.Length` for the whole duration of the flush, exactly as intended.
        var prelimContent = _prelimContent;
        _prelimContent = null;
        Insert(0, prelimContent);
    }

    internal override AbstractType InternalCopy()
    {
        return new YArray();
    }

    internal override AbstractType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var arr = new YArray();

        foreach (var item in EnumerateList())
        {
            if (item is AbstractType at)
            {
                arr.Add(new[] { at.InternalClone() });
            }
            else
            {
                arr.Add(new[] { item });
            }
        }

        return arr;
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YArrayRefId);
    }

    internal static YArray Read(IUpdateDecoder decoder)
    {
        return new YArray();
    }

    /// <summary>
    /// Creates YArrayEvent and calls observers.
    /// </summary>
    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YArrayEvent(this, transaction));
        }
    }

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
            _prelimContent.AddRange(content);
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
    /// (<c>this.map(c =&gt; c instanceof AbstractType ? c.toJSON() : c)</c>).
    /// </summary>
    public override object ToJson() => JsonTree(this);

    /// <summary>The JSON list of this array with each nested type left null and listed in <paramref name="nested"/>.</summary>
    internal override object ShallowJson(List<(AbstractType Type, object Container, string Key, int Index)> nested)
    {
        var result = new List<object>();
        foreach (var c in Map((c, i, arr) => c))
        {
            if (c is AbstractType at)
            {
                nested.Add((at, result, null, result.Count));
                result.Add(null);
            }
            else
            {
                result.Add(c);
            }
        }

        return result;
    }

    private IEnumerable<object> EnumerateList()
    {
        var n = _start;

        while (!n.IsNull)
        {
            var id = n.Id;
            var departures = Doc.Store.Departures;
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                foreach (var item in c)
                {
                    yield return item;
                }
            }

            n = Doc.Store.NextAfter(n, id, departures);
        }
    }
}
