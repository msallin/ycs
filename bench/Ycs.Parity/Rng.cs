namespace Ycs.Properties;

// A copy of the generator's PRNG in tests/Ycs.Properties/Generator.cs (that file needs the whole test project).
internal sealed class Rng(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);

    public int Range(int min, int max) => min + Int(max - min + 1);

    public bool Chance(double p) => (Next() >> 11) * (1.0 / (1UL << 53)) < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Int(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
