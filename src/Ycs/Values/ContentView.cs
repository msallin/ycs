using System.Collections;

namespace Ycs;

/// <summary>A read-only view of the values a content holds, as <see cref="FrozenList"/> hands them out (a byte array as a copy).</summary>
internal sealed class ContentView(IReadOnlyList<object?> items) : IReadOnlyList<object?>
{
    public int Count => items.Count;

    public object? this[int index] => ValueFreezer.OnRead(items[index]);

    public IEnumerator<object?> GetEnumerator()
    {
        for (var i = 0; i < items.Count; i++)
        {
            yield return ValueFreezer.OnRead(items[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
