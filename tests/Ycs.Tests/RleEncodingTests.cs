using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class RleEncodingTests
{
    [TestMethod]
    public void TestRleEncoder()
    {
        TestEncoder(new RleEncoder(), s => new RleDecoder(s));
    }

    [TestMethod]
    public void TestRleIntDiffEncoder()
    {
        TestEncoder(new RleIntDiffEncoder(0), s => new RleIntDiffDecoder(s, 0));
    }

    [TestMethod]
    public void TestIntDiffOptRleEncoder()
    {
        TestEncoder(new IntDiffOptRleEncoder(), s => new IntDiffOptRleDecoder(s));
    }

    /// <summary>
    /// The existing round-trip above only ever writes ascending values, so the encoder's negative
    /// branch is never exercised. These cases cover it, and check the bytes against lib0's reader
    /// rather than our own — a codec can be self-consistent and still disagree with Yjs.
    /// </summary>
    [TestMethod]
    public void TestIntDiffOptRleEncoderDescendingRuns()
    {
        var sequences = new[]
        {
            new long[] { 0, 1, 2, 3 },
            new long[] { 10, 8, 6, 4 },
            new long[] { 10, 8 },
            new long[] { 5, 3, 1, 1, 1, 9, 6, 3 },
            new long[] { 0, 100, 99, 98, 97, 200 },
        };

        foreach (var values in sequences)
        {
            byte[] data;
            var encoder = new IntDiffOptRleEncoder();
            foreach (var value in values)
            {
                encoder.Write(value);
            }

            data = encoder.ToArray();

            CollectionAssert.AreEqual(values, ReadLikeLib0(data, values.Length),
                $"lib0 would read [{string.Join(", ", values)}] back differently");

            var stream = new Lib0Reader(data);
            var decoder = new IntDiffOptRleDecoder(stream);
            foreach (var value in values)
            {
                Assert.AreEqual(value, decoder.Read());
            }
        }
    }

    /// <summary>
    /// lib0's <c>IntDiffOptRleDecoder.read()</c>, transcribed: the payload is
    /// <c>diff * 2 + hasCount</c>, and the diff is recovered with <c>Math.floor(payload / 2)</c>,
    /// which rounds away from zero for negatives.
    /// </summary>
    private static long[] ReadLikeLib0(byte[] encoded, int count)
    {
        var values = new List<long>(count);
        long state = 0;
        long diff = 0;
        uint remaining = 0;

        var stream = new Lib0Reader(encoded);
        for (int i = 0; i < count; i++)
        {
            if (remaining == 0)
            {
                var payload = stream.ReadVarInt().Value;
                var hasCount = (payload & 0x1) != 0;
                diff = payload >= 0 ? payload >> 1 : -((-payload + 1) >> 1);
                remaining = hasCount ? stream.ReadVarUint() + 2 : 1;
            }

            state += diff;
            remaining--;
            values.Add(state);
        }

        return values.ToArray();
    }

    [TestMethod]
    public void TestIntDiffEncoder()
    {
        TestEncoder(new IntDiffEncoder(0), s => new IntDiffDecoder(s, 0));
        TestEncoder(new IntDiffEncoder(42), s => new IntDiffDecoder(s, 42));
    }

    [TestMethod]
    public void TestStringEncoder()
    {
        const int n = 100;
        var words = new List<string>(n);
        var encoder = new StringEncoder();

        for (int i = 0; i < n; i++)
        {
            var v = Guid.NewGuid().ToString();
            words.Add(v);
            encoder.Write(v);
        }

        var data = encoder.ToArray();
        var stream = new Lib0Reader(data);
        var decoder = new StringDecoder(stream);

        for (int i = 0; i < words.Count; i++)
        {
            Assert.AreEqual(words[i], decoder.Read());
        }
    }

    [TestMethod]
    public void TestStringEncoderEmptyString()
    {
        const int n = 10;
        var encoder = new StringEncoder();

        for (int i = 0; i < n; i++)
        {
            encoder.Write(string.Empty);
        }

        var data = encoder.ToArray();
        var stream = new Lib0Reader(data);
        var decoder = new StringDecoder(stream);

        for (int i = 0; i < n; i++)
        {
            Assert.AreEqual(string.Empty, decoder.Read());
        }
    }

    private static void TestEncoder<TEncoder, TDecoder>(TEncoder encoder, Func<Lib0Reader, TDecoder> createDecoder, int n = 100)
        where TEncoder : IEncoder<long>
        where TDecoder : IDecoder<long>
    {
        for (int i = -n; i < n; i++)
        {
            encoder.Write(i);

            // Write additional 'i' times.
            for (int j = 0; j < i; j++)
            {
                encoder.Write(i);
            }
        }

        var data = encoder.GetWrittenSpan().ToArray();
        var stream = new Lib0Reader(data);
        var decoder = createDecoder(stream);

        for (int i = -n; i < n; i++)
        {
            Assert.AreEqual(i, decoder.Read());

            // Read additional 'i' times.
            for (int j = 0; j < i; j++)
            {
                Assert.AreEqual(i, decoder.Read());
            }
        }
    }

    private static void TestEncoder<TEncoder, TDecoder>(TEncoder encoder, Func<Lib0Reader, TDecoder> createDecoder, byte n = 100)
        where TEncoder : IEncoder<byte>
        where TDecoder : IDecoder<byte>
    {
        for (byte i = 0; i < n; i++)
        {
            encoder.Write(i);

            // Write additional 'i' times.
            for (byte j = 0; j < i; j++)
            {
                encoder.Write(i);
            }
        }

        var data = encoder.GetWrittenSpan().ToArray();
        var stream = new Lib0Reader(data);
        var decoder = createDecoder(stream);

        for (byte i = 0; i < n; i++)
        {
            Assert.AreEqual(i, decoder.Read());

            // Read additional 'i' times.
            for (byte j = 0; j < i; j++)
            {
                Assert.AreEqual(i, decoder.Read());
            }
        }
    }
}
