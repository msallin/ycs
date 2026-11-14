namespace Ycs.Fuzz;

/// <summary>
/// Turns a valid update into a damaged one. Deterministic for a given <see cref="Random"/>: the same
/// seed gives the same inputs, so a run is reproducible from its seed and iteration number.
/// </summary>
internal static class Mutator
{
    // Var-uint values that hit length and count fields: just past int, uint and the JavaScript
    // safe-integer range, plus the edges a careless "count - 1" or "(int)count" trips on.
    private static readonly ulong[] HugeValues =
    [
        0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x1_0000_0000, 0x1F_FFFF_FFFF_FFFF, 0x3FFF_FFFF, 0x0FFF_FFFF, 1 << 20,
    ];

    public static byte[] Mutate(byte[] input, Random random)
    {
        var data = input;
        var steps = 1 + random.Next(3);
        for (var i = 0; i < steps; i++)
        {
            data = random.Next(9) switch
            {
                0 => FlipBits(data, random),
                1 => Truncate(data, random),
                2 => InsertBytes(data, random),
                3 => HugeVarUint(data, random),
                4 => DuplicateChunk(data, random),
                5 => SwapChunks(data, random),
                6 => DeleteChunk(data, random),
                7 => OverwriteRandom(data, random),
                _ => random.Next(8) == 0 ? RandomBytes(random) : SetByte(data, random),
            };
        }

        return data;
    }

    private static byte[] FlipBits(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var result = (byte[])data.Clone();
        var flips = 1 + random.Next(4);
        for (var i = 0; i < flips; i++)
        {
            result[random.Next(result.Length)] ^= (byte)(1 << random.Next(8));
        }

        return result;
    }

    private static byte[] SetByte(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var result = (byte[])data.Clone();
        result[random.Next(result.Length)] = random.Next(4) switch
        {
            0 => 0,
            1 => 0xFF,
            2 => 0x7F,
            _ => (byte)random.Next(256),
        };
        return result;
    }

    private static byte[] Truncate(byte[] data, Random random) => data.Length == 0 ? data : data[..random.Next(data.Length)];

    private static byte[] InsertBytes(byte[] data, Random random)
    {
        var at = random.Next(data.Length + 1);
        var bytes = new byte[1 + random.Next(4)];
        random.NextBytes(bytes);
        return [.. data.AsSpan(0, at), .. bytes, .. data.AsSpan(at)];
    }

    /// <summary>Replaces the var-uint starting at a random offset with a huge one.</summary>
    private static byte[] HugeVarUint(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var at = random.Next(data.Length);
        var end = at;
        while (end < data.Length && (data[end] & 0x80) != 0)
        {
            end++;
        }

        end = Math.Min(end + 1, data.Length);
        var value = HugeValues[random.Next(HugeValues.Length)];
        return [.. data.AsSpan(0, at), .. VarUint(value), .. data.AsSpan(end)];
    }

    private static byte[] DuplicateChunk(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var (start, length) = Chunk(data, random);
        var at = random.Next(data.Length + 1);
        return [.. data.AsSpan(0, at), .. data.AsSpan(start, length), .. data.AsSpan(at)];
    }

    private static byte[] SwapChunks(byte[] data, Random random)
    {
        if (data.Length < 2)
        {
            return data;
        }

        var a = random.Next(data.Length - 1);
        var b = a + 1 + random.Next(data.Length - a - 1);
        var c = b + random.Next(data.Length - b + 1);

        // [0, a) [a, b) [b, c) [c, end) -> [0, a) [b, c) [a, b) [c, end)
        return [.. data.AsSpan(0, a), .. data.AsSpan(b, c - b), .. data.AsSpan(a, b - a), .. data.AsSpan(c)];
    }

    private static byte[] DeleteChunk(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var (start, length) = Chunk(data, random);
        return [.. data.AsSpan(0, start), .. data.AsSpan(start + length)];
    }

    private static byte[] OverwriteRandom(byte[] data, Random random)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var result = (byte[])data.Clone();
        var (start, length) = Chunk(data, random);
        random.NextBytes(result.AsSpan(start, length));
        return result;
    }

    private static byte[] RandomBytes(Random random)
    {
        var bytes = new byte[random.Next(64)];
        random.NextBytes(bytes);
        return bytes;
    }

    private static (int Start, int Length) Chunk(byte[] data, Random random)
    {
        var start = random.Next(data.Length);
        var length = 1 + random.Next(Math.Min(16, data.Length - start));
        return (start, length);
    }

    public static byte[] VarUint(ulong value)
    {
        var bytes = new List<byte>();
        while (value > 0x7F)
        {
            bytes.Add((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }
}
