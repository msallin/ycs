namespace Ycs.Server.Tests.Support;

/// <summary>xorshift64, as the fuzzers of <c>tests/Ycs.Tests</c>: the same seed gives the same run on every machine.</summary>
internal sealed class HostileRandom
{
    private ulong state;

    public HostileRandom(ulong seed)
    {
        state = (seed * 0x9E3779B97F4A7C15UL) + 0x1234567;
        Next();
        Next();
    }

    public ulong Next()
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }

    /// <summary>0 to <paramref name="n"/> - 1; 0 when <paramref name="n"/> is not positive.</summary>
    public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);

    public int Between(int min, int max) => min + Int(max - min + 1);

    public bool Chance(double p) => Next() % 10_000 < p * 10_000;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];

    public byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)Next();
        }

        return bytes;
    }

    /// <summary>
    /// A random string; a hostile one may cut an emoji in half, which the v1 encoding (UTF-8) turns into U+FFFD, so
    /// that the sender's own document differs from everyone else's, in Yjs too: an honest one does not.
    /// </summary>
    public string Text(int length, bool hostile = false)
    {
        var alphabet = hostile ? "abcdefghijklmnopqrstuvwxyz ä😀\n" : "abcdefghijklmnopqrstuvwxyz ä\n";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[Int(alphabet.Length)];
        }

        return new string(chars);
    }
}
