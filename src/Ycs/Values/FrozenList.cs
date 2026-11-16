using System.Collections;

namespace Ycs;

/// <summary>
/// An array value of a document: read-only to everyone but the code that builds it (<see cref="Append"/>, during a decode or a
/// copy). The elements are immutable values; a <c>byte[]</c> element is handed out as a copy, so no caller holds
/// an array the document holds.
/// </summary>
internal sealed class FrozenList : IList<object?>, IReadOnlyList<object?>, IList
{
    private object?[] _items;
    private int _count;

    /// <summary>An empty list with room for <paramref name="capacity"/> elements; a builder that knows the count makes the array exact.</summary>
    internal FrozenList(int capacity)
    {
        _items = capacity == 0 ? [] : new object?[capacity];
    }

    /// <summary>How many levels of lists and objects this value has, itself included; set by whoever builds it.</summary>
    internal int Depth { get; set; } = 1;

    public int Count => _count;

    public bool IsReadOnly => true;

    bool IList.IsFixedSize => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public object? this[int index]
    {
        get => ValueFreezer.OnRead(_items[index < _count ? index : throw new ArgumentOutOfRangeException(nameof(index))]);
        set => throw ValueFreezer.Frozen();
    }

    /// <summary>The element at <paramref name="index"/> as stored, a byte array included: not for a caller that keeps it.</summary>
    internal object? RawAt(int index) => _items[index < _count ? index : throw new ArgumentOutOfRangeException(nameof(index))];

    /// <summary>Enumerates the elements as stored (see <see cref="RawAt"/>).</summary>
    internal IEnumerator RawEnumerator() => new ArraySegment<object?>(_items, 0, _count).GetEnumerator();

    internal void Append(object? value)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, Math.Max(4, _count * 2));
        }

        _items[_count++] = value;
    }

    public IEnumerator<object?> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return ValueFreezer.OnRead(_items[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? item) => IndexOf(item) >= 0;

    public int IndexOf(object? item) => Array.IndexOf(_items, item, 0, _count);

    public void CopyTo(object?[] array, int arrayIndex)
    {
        for (var i = 0; i < _count; i++)
        {
            array[arrayIndex + i] = ValueFreezer.OnRead(_items[i]);
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < _count; i++)
        {
            array.SetValue(ValueFreezer.OnRead(_items[i]), index + i);
        }
    }

    public void Add(object? item) => throw ValueFreezer.Frozen();

    int IList.Add(object? value) => throw ValueFreezer.Frozen();

    public void Clear() => throw ValueFreezer.Frozen();

    public void Insert(int index, object? item) => throw ValueFreezer.Frozen();

    public bool Remove(object? item) => throw ValueFreezer.Frozen();

    void IList.Remove(object? value) => throw ValueFreezer.Frozen();

    public void RemoveAt(int index) => throw ValueFreezer.Frozen();
}
