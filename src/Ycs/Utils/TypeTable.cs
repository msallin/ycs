namespace Ycs;

/// <summary>
/// The types of a document by index, so that a slot names its parent in four bytes. An index is never reused: a
/// collected type's entry is cleared and stays, so no slot can name another type by an old index.
/// </summary>
internal sealed class TypeTable
{
    private YType?[] _types = [];
    private int _count;
    private int _collected;

    /// <summary>The indexes handed out, collected ones included.</summary>
    public int Count => _count;

    public int LiveCount => _count - _collected;

    /// <summary>The type at <paramref name="index"/>; null once collected.</summary>
    public YType? this[int index] => (uint)index < (uint)_count ? _types[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public int Add(YType type)
    {
        if (_count == _types.Length)
        {
            Array.Resize(ref _types, Math.Max(1, _count * 2));
        }

        _types[_count] = type;
        return _count++;
    }

    /// <summary>Puts the cleared entry of <paramref name="index"/> back, which only its own type can be.</summary>
    public void Restore(int index, YType type)
    {
        if (this[index] == null)
        {
            _types[index] = type;
            _collected--;
        }
    }

    /// <summary>Clears the entry; the index stays used. Collecting twice is a no-op.</summary>
    public void Collect(int index)
    {
        if (this[index] != null)
        {
            _types[index] = null;
            _collected++;
        }
    }
}
