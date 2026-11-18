using System;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StringSpanTests
{
    private static string ReadSplit(Lib0Reader reader, ref char[] chars)
    {
        var bound = reader.BeginVarString();
        if (chars.Length < bound)
        {
            chars = new char[bound];
        }

        return new string(chars, 0, reader.EndVarString(chars));
    }

    private static string ReadSplit(IUpdateDecoder decoder)
    {
        var chars = new char[decoder.ReadStringBound(default)];
        return new string(chars, 0, decoder.ReadStringChars(default, chars));
    }

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

        var expected = Assert.Throws<YUpdateException>(() => new Lib0Reader(input).ReadVarString());
        var reader = new Lib0Reader(input);
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(reader, ref chars));

        Assert.AreEqual((expected.Message, 0), (actual.Message, reader.Remaining));
    }

    [TestMethod]
    public void BeginVarString_TruncatedLength_ThrowsLikeReadVarString()
    {
        byte[] input = [5, (byte)'a', (byte)'b'];
        var chars = Array.Empty<char>();

        var expected = Assert.Throws<YUpdateException>(() => new Lib0Reader(input).ReadVarString());
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(new Lib0Reader(input), ref chars));

        Assert.AreEqual(expected.Message, actual.Message);
    }

    [TestMethod]
    public void BeginVarString_HugeLength_ThrowsLikeReadVarString()
    {
        byte[] input = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 1];
        var chars = Array.Empty<char>();

        var expected = Assert.Throws<YUpdateException>(() => new Lib0Reader(input).ReadVarString());
        var actual = Assert.Throws<YUpdateException>(() => ReadSplit(new Lib0Reader(input), ref chars));

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

        var actual = ReadSplit(new Lib0Reader(input), ref chars);

        Assert.AreEqual(new Lib0Reader(input).ReadVarString(), actual);
    }

    [TestMethod]
    public void BeginVarString_ManyStrings_AllocatesNothingPerString()
    {
        var utf8 = Encoding.UTF8.GetBytes("a string of some length € \U0001F600");
        var one = new byte[] { (byte)utf8.Length }.Concat(utf8).ToArray();
        var input = Enumerable.Range(0, 1000).SelectMany(_ => one).ToArray();
        var reader = new Lib0Reader(input);
        var chars = new char[64];
        reader.BeginVarString();
        reader.Reset(input, 0, input.Length);

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var i = 0; i < 1000; i++)
        {
            reader.BeginVarString();
            total += reader.EndVarString(chars);
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
        var decoder = new UpdateDecoderV2(new Lib0Reader(v2), long.MaxValue);
        var column = new UpdateDecoderV2(new Lib0Reader(v2), long.MaxValue);
        var count = 0;
        try
        {
            while (true)
            {
                column.ReadString(default);
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
            decoder.ReadStringBound(default);
            decoder.ReadStringChars(default, buffer);
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
        var a = new UpdateDecoderV2(new Lib0Reader(v2), long.MaxValue);
        var b = new UpdateDecoderV2(new Lib0Reader(v2), long.MaxValue);
        YUpdateException expected = null;
        try
        {
            while (true)
            {
                a.ReadString(default);
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
                ReadSplit(b);
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
        var split = new StringDecoder(new Lib0Reader(column));
        var whole = new StringDecoder(new Lib0Reader(column));
        var viaSplit = values.Select(_ =>
        {
            var chars = new char[split.BeginRead()];
            split.EndRead(chars);
            return new string(chars);
        }).ToArray();

        CollectionAssert.AreEqual(values, viaSplit);
        CollectionAssert.AreEqual(values, values.Select(_ => whole.Read()).ToArray());
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
            var whole = new StringDecoder(new Lib0Reader(column));
            var split = new StringDecoder(new Lib0Reader(column));
            var viaRead = values.Select(_ => whole.Read()).ToArray();
            var viaSplit = values.Select(_ => ReadSplit(split)).ToArray();

            CollectionAssert.AreEqual(expected, viaRead, $"Read, round {round}");
            CollectionAssert.AreEqual(expected, viaSplit, $"split, round {round}");
        }
    }

    private static string ReadSplit(StringDecoder decoder)
    {
        var chars = new char[decoder.BeginRead()];
        decoder.EndRead(chars);
        return new string(chars);
    }
}
