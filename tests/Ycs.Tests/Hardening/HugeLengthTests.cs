using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>A declared length or count of 2^32 - 1 is a <see cref="YUpdateException"/> at every site that reads one, never another exception type.</summary>
[TestClass]
public class HugeLengthTests
{
    private const string Huge = "FF FF FF FF 0F";

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static void AssertRejectedUnchanged(string updateHex)
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, "kept");
        var before = Convert.ToHexString(doc.EncodeStateAsUpdate());

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdateV1(Hex(updateHex)));
        Assert.AreEqual(before + "|kept", Convert.ToHexString(doc.EncodeStateAsUpdate()) + "|" + doc.GetText("t"));
    }

    [TestMethod]
    public void ReadVarString_HugeLength_IsRejected() =>
        Assert.ThrowsExactly<YUpdateException>(() => new Lib0Reader(Hex(Huge)).ReadVarString());

    [TestMethod]
    public void ReadVarStringSpan_HugeLength_IsRejected()
    {
        var reader = new Lib0Reader(Hex(Huge));
        var chars = new char[1];
        try
        {
            reader.ReadVarStringSpan(ref chars);
            Assert.Fail("no exception");
        }
        catch (YUpdateException)
        {
        }
    }

    [TestMethod]
    public void ReadVarUint8Array_HugeLength_IsRejected() =>
        Assert.ThrowsExactly<YUpdateException>(() => new Lib0Reader(Hex(Huge)).ReadVarUint8Array());

    [TestMethod]
    public void ReadVarUint8ArrayInto_HugeLength_IsRejected() =>
        Assert.ThrowsExactly<YUpdateException>(() => new Lib0Reader(Hex(Huge)).ReadVarUint8ArrayAsReader());

    [TestMethod]
    public void ApplyUpdate_HugeStringLength_IsRejectedAndDocUnchanged() =>
        AssertRejectedUnchanged("01 01 01 00 04 01 01 74 " + Huge + " 00");

    [TestMethod]
    public void ApplyUpdate_HugeBinaryLength_IsRejectedAndDocUnchanged() =>
        AssertRejectedUnchanged("01 01 01 00 03 " + Huge + " 00");

    [TestMethod]
    public void ReadAny_HugeCounts_AreRejected()
    {
        // 117 = array, 118 = object, 116 = Uint8Array, 119 = string.
        foreach (var type in new[] { "75", "76", "74", "77" })
        {
            var reader = new Lib0Reader(Hex(type + " " + Huge));
            Assert.ThrowsExactly<YUpdateException>(() => reader.ReadAny(), type);
        }
    }

    [TestMethod]
    public void ApplyUpdate_HugeStructCount_IsRejectedAndDocUnchanged() =>
        AssertRejectedUnchanged("01 " + Huge + " 01 00 04 01 01 74 01 61 00");

    [TestMethod]
    public void ApplyUpdate_HugeClientCount_IsRejectedAndDocUnchanged() =>
        AssertRejectedUnchanged(Huge + " 01 01 00 04 01 01 74 01 61 00");

    [TestMethod]
    public void ApplyUpdate_HugeDeleteSetCounts_AreRejectedAndDocUnchanged()
    {
        AssertRejectedUnchanged("00 " + Huge);
        AssertRejectedUnchanged("00 01 01 " + Huge);
    }

    [TestMethod]
    public void StringDecoderRead_HugeLengthInColumn_IsRejected()
    {
        // An empty string column, then one length of 2^32 - 1.
        var decoder = new StringDecoder(new Lib0Reader(Hex("00 BF FF FF FF 1F")));

        Assert.ThrowsExactly<YUpdateException>(() => decoder.Read());
    }
}
