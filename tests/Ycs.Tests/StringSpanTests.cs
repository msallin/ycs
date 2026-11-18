using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StringSpanTests
{
    private static string ReadSplit(ref Lib0Reader reader, byte[] input, ref char[] chars)
    {
        var bound = reader.BeginVarString(input);
        if (chars.Length < bound)
        {
            chars = new char[bound];
        }

        return new string(chars, 0, reader.EndVarString(input, chars));
    }

    private static string ReadSplit<TDecoder>(ref TDecoder decoder, byte[] input)
        where TDecoder : struct, IUpdateDecoder
    {
        var chars = new char[decoder.ReadStringBound(input)];
        return new string(chars, 0, decoder.ReadStringChars(input, chars));
    }

    private static Lib0Reader ReaderOf(byte[] input) => new(0, input.Length);

    private static byte[] VarString(params byte[] utf8) => [(byte)utf8.Length, .. utf8];

    [TestMethod]
    [DataRow(new byte[] { 0xC0, 0xAF }, DisplayName = "overlong slash")]
    [DataRow(new byte[] { 0xE0, 0x80, 0xAF }, DisplayName = "overlong three bytes")]
    [DataRow(new byte[] { 0xED, 0xA0, 0x80 }, DisplayName = "encoded high surrogate")]
    [DataRow(new byte[] { 0xED, 0xB0, 0x80 }, DisplayName = "encoded low surrogate")]
    [DataRow(new byte[] { 0xE2, 0x82 }, DisplayName = "truncated sequence")]
    [DataRow(new byte[] { 0xF0, 0x9F, 0x98 }, DisplayName = "truncated four bytes")]
    [DataRow(new byte[] { 0x80 }, DisplayName = "lone continuation")]
    [DataRow(new byte[] { 0xF5, 0x80, 0x80, 0x80 }, DisplayName = "above U+10FFFF")]
    [DataRow(new byte[] { 0xFF }, DisplayName = "invalid byte")]
    public void BeginVarString_InvalidUtf8_ThrowsLikeReadVarString(byte[] utf8)
    {
        var input = VarString(utf8);
        var chars = Array.Empty<char>();

        var whole = ReaderOf(input);
        var expected = Assert.Throws<YUpdateException>(() => whole.ReadVarString(input));
        var reader = ReaderOf(input);
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(ref reader, input, ref chars));

        Assert.AreEqual((expected.Message, 0), (actual.Message, reader.Remaining));
    }

    [TestMethod]
    public void BeginVarString_TruncatedLength_ThrowsLikeReadVarString()
    {
        byte[] input = [5, (byte)'a', (byte)'b'];
        var chars = Array.Empty<char>();

        var whole = ReaderOf(input);
        var split = ReaderOf(input);
        var expected = Assert.Throws<YUpdateException>(() => whole.ReadVarString(input));
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(ref split, input, ref chars));

        Assert.AreEqual(expected.Message, actual.Message);
    }

    [TestMethod]
    public void BeginVarString_HugeLength_ThrowsLikeReadVarString()
    {
        byte[] input = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 1];
        var chars = Array.Empty<char>();

        var whole = ReaderOf(input);
        var split = ReaderOf(input);
        var expected = Assert.Throws<YUpdateException>(() => whole.ReadVarString(input));
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(ref split, input, ref chars));

        Assert.AreEqual(expected.Message, actual.Message);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("héllo € \U0001F600 ﻿")]
    public void BeginVarString_ValidString_EqualsReadVarString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var input = new byte[] { (byte)utf8.Length }.Concat(utf8).ToArray();
        var chars = Array.Empty<char>();

        var split = ReaderOf(input);
        var whole = ReaderOf(input);
        var actual = ReadSplit(ref split, input, ref chars);

        Assert.AreEqual(whole.ReadVarString(input), actual);
    }

    [TestMethod]
    public void BeginVarString_ManyStrings_AllocatesNothingPerString()
    {
        var utf8 = Encoding.UTF8.GetBytes("a string of some length € \U0001F600");
        var one = new byte[] { (byte)utf8.Length }.Concat(utf8).ToArray();
        var input = Enumerable.Range(0, 1000).SelectMany(_ => one).ToArray();
        var reader = ReaderOf(input);
        var chars = new char[64];
        var warm = ReaderOf(input);
        warm.BeginVarString(input);

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var i = 0; i < 1000; i++)
        {
            reader.BeginVarString(input);
            total += reader.EndVarString(input, chars);
        }

        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual((0L, 1000 * 28), (allocated, total));
    }

    [TestMethod]
    public void ReadStringChars_V2ManyStrings_AllocatesNothingPerString()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        for (var i = 0; i < 300; i++)
        {
            doc.Transact(_ => text.Insert(0, "word" + i));
        }

        var v2 = EncodingUtils.EncodeStateAsUpdate(doc.Store, default, true);
        var decoder = new UpdateDecoderV2(v2, long.MaxValue, int.MaxValue, []);
        var column = new UpdateDecoderV2(v2, long.MaxValue, int.MaxValue, []);
        var count = 0;
        try
        {
            while (true)
            {
                column.ReadString(v2);
                count++;
            }
        }
        catch (YUpdateException)
        {
        }

        var buffer = new char[64];
        var before = System.GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++)
        {
            decoder.ReadStringBound(v2);
            decoder.ReadStringChars(v2, buffer);
        }

        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void ReadStringChars_V2LongerThanColumn_ThrowsLikeReadString()
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, "abc");
        var v2 = EncodingUtils.EncodeStateAsUpdate(doc.Store, default, true);
        var a = new UpdateDecoderV2(v2, long.MaxValue, int.MaxValue, []);
        var b = new UpdateDecoderV2(v2, long.MaxValue, int.MaxValue, []);
        YUpdateException expected = null;
        try
        {
            while (true)
            {
                a.ReadString(v2);
            }
        }
        catch (YUpdateException e)
        {
            expected = e;
        }

        YUpdateException actual = null;
        try
        {
            while (true)
            {
                ReadSplit(ref b, v2);
            }
        }
        catch (YUpdateException e)
        {
            actual = e;
        }

        Assert.AreEqual(expected.Message, actual.Message);
    }

    [TestMethod]
    [DataRow("a", "bc", "d", DisplayName = "ascii")]
    [DataRow("h\u00e9", "\u20ac", "x\U0001F600", "", "y", DisplayName = "multi-byte and a pair")]
    [DataRow("q\uD83D", "\uDE00z", "\U0001F600\U0001F601", DisplayName = "a length inside a pair")]
    [DataRow("\uD83D", "\uDE00\uD83D", "\uDE01", DisplayName = "every length inside a pair")]
    [DataRow("ab", "\uD83D", "\uDE00c", DisplayName = "bytes, then a split pair")]
    [DataRow("\u00e9\u20ac", "\uD83D", "\uDE00", "\U0001F600", DisplayName = "multi-byte, then a split pair, then a pair")]
    public void StringDecoder_SeveralValues_ReadAsWritten(params string[] values)
    {
        var encoder = new StringEncoder();
        foreach (var value in values)
        {
            encoder.Write(value);
        }

        var column = encoder.ToArray();
        var split = new StringDecoder(column, ReaderOf(column));
        var whole = new StringDecoder(column, ReaderOf(column));
        var viaSplit = values.Select(_ => ReadSplit(ref split, column)).ToArray();

        CollectionAssert.AreEqual(values, viaSplit);
        CollectionAssert.AreEqual(values, values.Select(_ => whole.Read(column)).ToArray());
    }

    [TestMethod]
    public void ReadStringChars_V1AndV2WithTextItems_StageTheText()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ascii");
        text.Insert(5, " h\u00e9llo \u20ac");
        text.Insert(0, "\U0001F600 pair ");
        var expected = text.ToString();

        foreach (var v2 in new[] { false, true })
        {
            var update = EncodingUtils.EncodeStateAsUpdate(doc.Store, default, v2);
            var other = new YDoc();
            other.ApplyUpdate(update, v2 ? UpdateFormat.V2 : UpdateFormat.V1);

            Assert.AreEqual(expected, other.GetText("t").ToString());
        }
    }

    [TestMethod]
    public void StringDecoder_RandomColumns_MatchTheJoinedStringSlicedByLength()
    {
        // The oracle shares no code with the decoder: the values joined and round-tripped through UTF-8 (a lone half
        // becomes U+FFFD, a pair split over two values is rejoined), then cut at each value's length.
        var random = new Random(7);
        string[] alphabet = ["a", "z", "\u00e9", "\u20ac", "\uFEFF", "\U0001F600", "\uD83D", "\uDE00"];
        for (var round = 0; round < 3000; round++)
        {
            var values = Enumerable.Range(0, random.Next(0, 6))
                .Select(_ => string.Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => alphabet[random.Next(alphabet.Length)])))
                .ToArray();
            var encoder = new StringEncoder();
            foreach (var value in values)
            {
                encoder.Write(value);
            }

            var joined = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(string.Concat(values)));
            var expected = new string[values.Length];
            var at = 0;
            for (var i = 0; i < values.Length; i++)
            {
                expected[i] = joined.Substring(at, values[i].Length);
                at += values[i].Length;
            }

            var column = encoder.ToArray();
            var whole = new StringDecoder(column, ReaderOf(column));
            var split = new StringDecoder(column, ReaderOf(column));
            var viaRead = values.Select(_ => whole.Read(column)).ToArray();
            var viaSplit = values.Select(_ => ReadSplit(ref split, column)).ToArray();

            CollectionAssert.AreEqual(expected, viaRead, $"Read, round {round}");
            CollectionAssert.AreEqual(expected, viaSplit, $"split, round {round}");
        }
    }

    private static string ReadSplit(ref StringDecoder decoder, byte[] input)
    {
        var chars = new char[decoder.BeginRead(input)];
        decoder.EndRead(input, chars);
        return new string(chars);
    }
}
