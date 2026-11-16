using System.Collections;

namespace Ycs;

/// <summary>
/// An array value of a document: read-only to everyone but the code that builds it (<see cref="Append"/>, during a decode or a
/// copy). The elements are immutable values; a <c>byte[]</c> element is handed out as a copy, so no caller holds
/// an array the document holds.
/// </summary>
internal sealed class FrozenList : IList<object?>, IReadOnlyList<object?>, IList
{
    private readonly List<object?> _items;

    internal FrozenList(int capacity)
    {
        _items = new List<object?>(capacity);
    }

    /// <summary>How many levels of lists and objects this value has, itself included; set by whoever builds it.</summary>
    internal int Depth { get; set; } = 1;

    public int Count => _items.Count;

    public bool IsReadOnly => true;

    bool IList.IsFixedSize => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    public object? this[int index]
    {
        get => ValueFreezer.OnRead(_items[index]);
        set => throw ValueFreezer.Frozen();
    }

    /// <summary>The element at <paramref name="index"/> as stored, a byte array included: not for a caller that keeps it.</summary>
    internal object? RawAt(int index) => _items[index];

    /// <summary>Enumerates the elements as stored (see <see cref="RawAt"/>).</summary>
    internal IEnumerator RawEnumerator() => _items.GetEnumerator();

    internal void Append(object? value) => _items.Add(value);

    public IEnumerator<object?> GetEnumerator()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            yield return ValueFreezer.OnRead(_items[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(object? item) => IndexOf(item) >= 0;

    public int IndexOf(object? item) => _items.IndexOf(item);

    public void CopyTo(object?[] array, int arrayIndex)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            array[arrayIndex + i] = ValueFreezer.OnRead(_items[i]);
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < _items.Count; i++)
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
