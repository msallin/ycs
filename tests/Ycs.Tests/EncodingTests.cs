// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class EncodingTests
{
    /// <summary>
    /// Check if binary encoding is compatible with golang binary encoding.
    /// Result: is compatible up to 32 bit: [0, 4294967295] / [0, 0xFFFFFFFF].
    /// </summary>
    [DataTestMethod]
    [DataRow(0u, new byte[] { 0 })]
    [DataRow(1u, new byte[] { 1 })]
    [DataRow(128u, new byte[] { 128, 1 })]
    [DataRow(200u, new byte[] { 200, 1 })]
    [DataRow(32u, new byte[] { 32 })]
    [DataRow(500u, new byte[] { 244, 3 })]
    [DataRow(256u, new byte[] { 128, 2 })]
    [DataRow(700u, new byte[] { 188, 5 })]
    [DataRow(1024u, new byte[] { 128, 8 })]
    [DataRow(1025u, new byte[] { 129, 8 })]
    [DataRow(4048u, new byte[] { 208, 31 })]
    [DataRow(5050u, new byte[] { 186, 39 })]
    [DataRow(1_000_000u, new byte[] { 192, 132, 61 })]
    [DataRow(34_951_959u, new byte[] { 151, 166, 213, 16 })]
    [DataRow(2_147_483_646u, new byte[] { 254, 255, 255, 255, 7 })]
    [DataRow(2_147_483_647u, new byte[] { 255, 255, 255, 255, 7 })]
    [DataRow(2_147_483_648u, new byte[] { 128, 128, 128, 128, 8 })]
    [DataRow(2_147_483_700u, new byte[] { 180, 128, 128, 128, 8 })]
    [DataRow(4_294_967_294u, new byte[] { 254, 255, 255, 255, 15 })]
    [DataRow(4_294_967_295u, new byte[] { 255, 255, 255, 255, 15 })]
    public void TestGolangBinaryEncodingCompatibility(uint value, byte[] expected)
    {
        var stream = new Lib0Writer();
        stream.WriteVarUint(value);

        var actual = stream.ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void TestEncodeMax32BitUint()
    {
        DoTestEncoding<uint>("Max 32bit uint", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), Bits.Bits32);
    }

    [TestMethod]
    public void TestVarUintEncoding()
    {
        DoTestEncoding<uint>("VarUint 1 byte", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), 42);
        DoTestEncoding<uint>("VarUint 2 bytes", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), 1 << 9 | 3);
        DoTestEncoding<uint>("VarUint 3 bytes", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), 1 << 17 | 1 << 9 | 3);
        DoTestEncoding<uint>("VarUint 4 bytes", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), 1 << 25 | 1 << 17 | 1 << 9 | 3);
        DoTestEncoding<uint>("VarUint of 2839012934", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), 2_839_012_934);
    }

    [TestMethod]
    public void TestVarIntEncoding()
    {
        DoTestEncoding<long>("VarInt 1 byte", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, -42);
        DoTestEncoding<long>("VarInt 2 bytes", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, -(1 << 9 | 3));
        DoTestEncoding<long>("VarInt 3 bytes", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, -(1 << 17 | 1 << 9 | 3));
        DoTestEncoding<long>("VarInt 4 bytes", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, -(1 << 25 | 1 << 17 | 1 << 9 | 3));
        DoTestEncoding<long>("VarInt of -691529286", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, -691_529_286);
        DoTestEncoding<long>("VarInt of 64 (-0)", (w, v) => w.WriteVarInt(v, negativeZero: true), (r) => r.ReadVarInt().Value, 0);
    }

    /// <summary>
    /// The 'any' encoding follows JavaScript's number rules, so a value written by Ycs has the
    /// bytes lib0 writes for the same value. Expected bytes from lib0 0.2.117's writeAny (Node 24).
    /// </summary>
    public static IEnumerable<object[]> AnyValuesAndLib0Bytes => new[]
    {
        new object[] { 2, "7d02" },
        new object[] { 2.0, "7d02" },
        new object[] { 2.0f, "7d02" },
        new object[] { -2L, "7d42" },
        new object[] { 1.5, "7c3fc00000" },
        new object[] { 0.1, "7b3fb999999999999a" },
        new object[] { 2147483647, "7dbfffffff0f" },
        new object[] { 2147483648L, "7c4f000000" },
        new object[] { -2147483648, "7ccf000000" },
        new object[] { 9007199254740991L, "7b433fffffffffffff" },
        new object[] { -0.0, "7d40" },
        new object[] { double.NaN, "7b7ff8000000000000" },
        new object[] { double.PositiveInfinity, "7c7f800000" },
        new object[] { 1e300, "7b7e37e43c8800759c" },
        new object[] { new BigInteger(5), "7a0000000000000005" },
        new object[] { new Dictionary<string, object> { ["a"] = 1, ["b"] = new object[] { true, null, "x" } }, "760201617d0101627503787e770178" },
        new object[] { new byte[] { 1, 2, 3 }, "7403010203" },
    };

    [TestMethod]
    [DynamicData(nameof(AnyValuesAndLib0Bytes))]
    public void TestWriteAnyMatchesLib0(object value, string expectedHex)
    {
        var writer = new Lib0Writer();
        writer.WriteAny(value);
        Assert.AreEqual(expectedHex, Convert.ToHexStringLower(writer.WrittenSpan));
    }

    [TestMethod]
    public void TestReadAnyProducesTheClosedSetOfValues()
    {
        object RoundTrip(object value)
        {
            var writer = new Lib0Writer();
            writer.WriteAny(value);
            return new Lib0Reader(writer.ToArray()).ReadAny();
        }

        Assert.AreEqual(2, RoundTrip(2.0));
        Assert.AreEqual(1.5, RoundTrip(1.5f));
        Assert.AreEqual(2147483648.0, RoundTrip(2147483648L));
        Assert.IsTrue(double.IsNegative((double)RoundTrip(-0.0)));
        Assert.AreEqual(new BigInteger(-7), RoundTrip(new BigInteger(-7)));
        CollectionAssert.AreEqual(new List<object> { "a", 1, null }, (List<object>)RoundTrip(new object[] { "a", 1, null }));
    }

    [TestMethod]
    public void TestWriteAnyRejectsValuesOutsideTheClosedSet()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new Lib0Writer().WriteAny(9007199254740992L));
        Assert.ThrowsException<NotSupportedException>(() => new Lib0Writer().WriteAny(DateTime.UnixEpoch));
        Assert.ThrowsException<NotSupportedException>(() => AnyEncoding.Validate(new List<object> { new object() }));

        // ...and a document rejects them when they are inserted, not when it is encoded.
        var doc = new YDoc();
        Assert.ThrowsException<NotSupportedException>(() => doc.GetMap("map").Set("k", Guid.Empty));
        Assert.AreEqual(0, doc.GetMap("map").Count);
    }

    [TestMethod]
    public void TestVarIntEncodingNegativeZero()
    {
        var stream = new Lib0Writer();
        stream.WriteVarInt(0, negativeZero: true);

        var actual = stream.ToArray();

        // '-0' should have the 7th bit set, i.e. == 64.
        CollectionAssert.AreEqual(new byte[] { 64 }, actual);

        var inputStream = new Lib0Reader(actual);
        var v = inputStream.ReadVarInt();
        Assert.AreEqual(0, v.Value);
        Assert.AreEqual(-1, v.Sign);
    }

    [TestMethod]
    public void TestRepeatVarUintEncoding()
    {
        var rand = new Random();
        for (int i = 0; i < 10; i++)
        {
            var n = rand.Next(0, (1 << 28) - 1);
            DoTestEncoding<uint>($"VarUint of {n}", (w, v) => w.WriteVarUint(v), (r) => r.ReadVarUint(), (uint)n);
        }
    }

    [TestMethod]
    public void TestRepeatVarIntEncoding()
    {
        var rand = new Random();
        for (int i = 0; i < 10; i++)
        {
            var n = rand.Next(0, int.MaxValue);
            DoTestEncoding<long>($"VarInt of {n}", (w, v) => w.WriteVarInt(v), (r) => r.ReadVarInt().Value, n);
        }
    }

    [TestMethod]
    public void TestStringEncoding()
    {
        DoTestEncoding<string>(string.Empty, (w, v) => w.WriteVarString(v), (r) => r.ReadVarString(), "hello");
        DoTestEncoding<string>(string.Empty, (w, v) => w.WriteVarString(v), (r) => r.ReadVarString(), string.Empty);
        DoTestEncoding<string>(string.Empty, (w, v) => w.WriteVarString(v), (r) => r.ReadVarString(), "쾟");
        DoTestEncoding<string>(string.Empty, (w, v) => w.WriteVarString(v), (r) => r.ReadVarString(), "龟"); // Surrogate length 3.
        DoTestEncoding<string>(string.Empty, (w, v) => w.WriteVarString(v), (r) => r.ReadVarString(), "😝"); // Surrogate length 4.
    }

    private void DoTestEncoding<T>(string description, Action<Lib0Writer, T> write, Func<Lib0Reader, T> read, T val)
    {
        byte[] encoded;

        var outputStream = new Lib0Writer();
        write(outputStream, val);
        encoded = outputStream.ToArray();

        var inputStream = new Lib0Reader(encoded);
        var decodedValue = read(inputStream);
        Assert.AreEqual(val, decodedValue, description);
    }
}
