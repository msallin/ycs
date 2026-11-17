namespace Ycs;

/// <summary>
/// The types of a document by index, so that a slot names its parent in four bytes. An index is never reused: a
/// collected type's entry is cleared and stays, so no slot can name another type by an old index.
/// </summary>
internal sealed class TypeTable
{
    private readonly List<YType?> _types = [];
    private int _collected;

    /// <summary>The indexes handed out, collected ones included.</summary>
    public int Count => _types.Count;

    public int LiveCount => _types.Count - _collected;

    /// <summary>The type at <paramref name="index"/>; null once collected.</summary>
    public YType? this[int index] => _types[index];

    public int Add(YType type)
    {
        _types.Add(type);
        return _types.Count - 1;
    }

    /// <summary>Puts the cleared entry of <paramref name="index"/> back, which only its own type can be.</summary>
    public void Restore(int index, YType type)
    {
        if (_types[index] == null)
        {
            _types[index] = type;
            _collected--;
        }
    }

    /// <summary>Clears the entry; the index stays used. Collecting twice is a no-op.</summary>
    public void Collect(int index)
    {
        if (_types[index] != null)
        {
            _types[index] = null;
            _collected++;
        }
    }
}
