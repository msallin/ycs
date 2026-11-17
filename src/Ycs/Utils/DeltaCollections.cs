#nullable enable

using System.Collections;

namespace Ycs;

/// <summary>
/// The items an <see cref="Delta.InsertItems"/> holds: the stored values of the inserted content, as <see cref="YValue"/> when
/// read. A list of <see cref="YValue"/> would be three times the bytes of the stored values.
/// </summary>
internal sealed class StoredItems : IReadOnlyList<YValue>
{
    private object?[] _items = new object?[1];
    private int _count;

    public int Count => _count;

    public YValue this[int index] => index >= 0 && index < _count
        ? YValue.FromStored(_items[index])
        : throw new ArgumentOutOfRangeException(nameof(index), index, $"The operation inserts {_count} items.");

    public void AddRange(IReadOnlyList<object> values)
    {
        if (_count + values.Count > _items.Length)
        {
            Array.Resize(ref _items, Math.Max(_count + values.Count, _items.Length * 2));
        }

        for (var i = 0; i < values.Count; i++)
        {
            _items[_count++] = values[i];
        }
    }

    // The values as ContentView reads them (a byte array as a copy), taken at once from the arena.
    public void AddRange(ReadOnlySpan<object> values)
    {
        if (_count + values.Length > _items.Length)
        {
            Array.Resize(ref _items, Math.Max(_count + values.Length, _items.Length * 2));
        }

        for (var i = 0; i < values.Length; i++)
        {
            _items[_count++] = ValueFreezer.OnRead(values[i]);
        }
    }

    public IEnumerator<YValue> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return YValue.FromStored(_items[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
