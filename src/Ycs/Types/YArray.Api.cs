// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;

namespace Ycs;

/// <summary>
/// A shared array of <see cref="YValue"/>s. Indices are those of the visible values; a range of them is taken with
/// <c>array[1..3]</c>. Inserting a <see cref="YValue"/> copies it into the array: the value a caller holds never changes
/// what the document holds.
/// </summary>
public sealed partial class YArray : IReadOnlyList<YValue>
{
    /// <summary>
    /// Observes the events of this array (Yjs' <c>observe(f)</c>). Disposing the result unsubscribes.
    /// </summary>
    public IDisposable Observe(Action<YArrayEvent> handler) => AddObserver(handler);

    public YArray()
    {
        _prelimContent = new List<object>();
    }

    public YArray(params ReadOnlySpan<YValue> values)
    {
        _prelimContent = new List<object>(values.Length);
        foreach (var value in values)
        {
            _prelimContent.Add(value.Stored());
        }
    }

    public int Count => Length;

    internal override int Length => _prelimContent?.Count ?? base.Length;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in 0 to <see cref="Count"/> - 1.</exception>
    public YValue this[int index]
    {
        get
        {
            CheckIndex(index);
            return YValue.FromStored(RawAt(index));
        }
    }

    /// <summary>The value at <paramref name="index"/>; false, and null, when the array has none.</summary>
    public bool TryGetValue(int index, out YValue value)
    {
        if ((uint)index >= (uint)Count)
        {
            value = default;
            return false;
        }

        value = YValue.FromStored(RawAt(index));
        return true;
    }

    public YArray Clone() => (YArray)InternalClone();

    /// <summary>
    /// Inserts <paramref name="values"/> at <paramref name="index"/>, which is 0 to <see cref="Count"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is beyond <see cref="Count"/>; nothing is inserted.</exception>
    public void Insert(int index, params ReadOnlySpan<YValue> values)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index is beyond the count ({Count}). Nothing was inserted.");
        }

        var content = Stored(values);
        InsertRaw(index, content);
        Release(content);
    }

    /// <summary>
    /// Appends <paramref name="values"/> after the last item, deleted ones included, as Yjs' <c>push</c> (which is not
    /// an insert at <see cref="Count"/>: the bytes and the place among concurrent appends differ).
    /// </summary>
    public void Add(params ReadOnlySpan<YValue> values)
    {
        var content = Stored(values);
        AddRaw(content);
        Release(content);
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in 0 to <see cref="Count"/> - 1.</exception>
    public void RemoveAt(int index)
    {
        CheckIndex(index);
        RemoveRangeCore(index, 1);
    }

    /// <exception cref="ArgumentOutOfRangeException">The range is not within the array; nothing is removed.</exception>
    public void RemoveRange(int index, int count)
    {
        CheckRange(index, count, nameof(index));
        RemoveRangeCore(index, count);
    }

    /// <summary>The <paramref name="length"/> values from <paramref name="start"/>; <c>array[1..3]</c> calls it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the array.</exception>
    public YValue[] Slice(int start, int length)
    {
        CheckRange(start, length, nameof(start));
        var result = new YValue[length];
        if (length == 0)
        {
            return result;
        }

        if (_prelimContent != null)
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = YValue.FromStored(_prelimContent[start + i]);
            }

            return result;
        }

        var slice = InternalSlice(start, start + length);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = YValue.FromStored(slice[i]);
        }

        return result;
    }

    public YValue[] ToArray()
    {
        var result = new YValue[Count];
        var i = 0;
        foreach (var raw in EnumerateValues())
        {
            result[i++] = YValue.FromStored(raw);
        }

        return result;
    }

    /// <summary>
    /// The values visible in <paramref name="snapshot"/> (Yjs' <c>typeListToArraySnapshot</c>). It only reads: an item that
    /// was merged across the snapshot's boundary is seen whole, as in Yjs.
    /// </summary>
    public YValue[] ToArray(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raw = ContentAtSnapshot(snapshot);
        var result = new YValue[raw.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = YValue.FromStored(raw[i]);
        }

        return result;
    }

    public IEnumerator<YValue> GetEnumerator()
    {
        foreach (var raw in EnumerateValues())
        {
            yield return YValue.FromStored(raw);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Transforms this Shared Type to a JSON-compatible list. Mirrors Yjs' <c>toJSON()</c>
    /// (<c>this.map(c =&gt; c instanceof YType ? c.toJSON() : c)</c>).
    /// </summary>
    public override object ToJson() => JsonTree(this);

    internal void InsertRaw(int index, ICollection<object?> content)
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

    internal void AddRaw(ICollection<object?> content)
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

    // The array of a one-value insert, kept for the next one on this thread: an insert only reads it before it returns, no
    // user code runs in between, and an insert that runs inside another finds the slot empty and allocates.
    [ThreadStatic]
    private static object?[]? t_single;

    private static object?[] Stored(ReadOnlySpan<YValue> values)
    {
        object?[] stored;
        if (values.Length == 1)
        {
            stored = t_single ?? new object?[1];
            t_single = null;
        }
        else
        {
            stored = new object?[values.Length];
        }

        for (var i = 0; i < stored.Length; i++)
        {
            stored[i] = values[i].Stored();
        }

        return stored;
    }

    private static void Release(object?[] stored)
    {
        if (stored.Length == 1)
        {
            stored[0] = null;
            t_single = stored;
        }
    }

    private void RemoveRangeCore(int index, int count)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, count), static (s, tr) => s.self.Delete(tr, s.index, s.count));
        }
        else
        {
            PrelimDelete(_prelimContent, index, count);
        }
    }

    private object? RawAt(int index) =>
        _prelimContent != null ? _prelimContent[index] : TryGetRawAt(index, out var value) ? value : null;

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index is not within the array (count {Count}).");
        }
    }

    private void CheckRange(int start, int length, string paramName)
    {
        if (start < 0 || length < 0 || start > Count || length > Count - start)
        {
            throw new ArgumentOutOfRangeException(paramName, start, $"The range of {length} values from {start} is not within the array (count {Count}).");
        }
    }
}
