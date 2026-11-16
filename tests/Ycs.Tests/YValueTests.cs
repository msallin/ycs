using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YValueTests
{
    private const double TwoTo53 = 9007199254740992.0;

    private static YValue Decoded(object? inserted, bool v2)
    {
        var a = new YDoc();
        a.GetMap("m")["k"] = YValue.FromObject(inserted!);
        var b = new YDoc();
        if (v2)
        {
            b.ApplyUpdate(a.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);
        }
        else
        {
            b.ApplyUpdate(a.EncodeStateAsUpdate());
        }

        return YValue.FromObject(b.GetMap("m")["k"].ToObject());
    }

    [TestMethod]
    public void Default_IsNull()
    {
        Assert.AreEqual((YValueKind.Null, true, "null"), (default(YValue).Kind, default(YValue).IsNull, default(YValue).ToString()));
        Assert.IsNull(default(YValue).ToObject());
    }

    [TestMethod]
    public void ImplicitConversions_EachType_GivesItsKind()
    {
        YValue[] values = [true, 1, 2L, 3.5, new BigInteger(4), "s", new byte[] { 1 }, (string?)null, (byte[]?)null, new YMap(), new YDoc()];

        CollectionAssert.AreEqual(
            new[]
            {
                YValueKind.Boolean, YValueKind.Number, YValueKind.Number, YValueKind.Number, YValueKind.BigInteger, YValueKind.String,
                YValueKind.Bytes, YValueKind.Null, YValueKind.Null, YValueKind.SharedType, YValueKind.Document,
            },
            values.Select(v => v.Kind).ToArray());
    }

    [TestMethod]
    public void ImplicitFromBytes_CallerChangesTheArray_ValueKeepsItsCopy()
    {
        var bytes = new byte[] { 1, 2, 3 };
        YValue value = bytes;

        bytes[0] = 9;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, value.GetBytes());
    }

    [TestMethod]
    public void GetBytes_CallerChangesTheResult_ValueKeepsItsBytes()
    {
        YValue value = new byte[] { 1, 2, 3 };

        value.GetBytes()[0] = 9;
        ((byte[])value.ToObject()!)[1] = 9;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, value.GetBytes());
    }

    [TestMethod]
    public void ImplicitFromLong_BeyondTheSafeIntegers_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => { YValue v = TwoTo53 > 0 ? (long)TwoTo53 : 0; });
        Assert.AreEqual(9007199254740991L, ((YValue)9007199254740991L).GetInt64());
        Assert.AreEqual(-9007199254740991L, ((YValue)(-9007199254740991L)).GetInt64());
    }

    [TestMethod]
    public void ImplicitFromBigInteger_Beyond64Bits_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => { YValue v = BigInteger.Pow(2, 63); });
        Assert.AreEqual(long.MinValue, (long)(BigInteger)(YValue)new BigInteger(long.MinValue));
        Assert.AreEqual(new BigInteger(long.MaxValue), ((YValue)new BigInteger(long.MaxValue)).GetBigInteger());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Numbers_AnyWireForm_ReadAsDoubleAndIntegralAsInt64(bool v2)
    {
        // 3: an integer; 2^40: written as a float; 1.5 and 0.1f: float32 and float64; each decoded by the document.
        var three = Decoded(3, v2);
        var big = Decoded(1L << 40, v2);
        var half = Decoded(1.5, v2);
        var single = Decoded(0.1f, v2);

        Assert.AreEqual(
            (true, 3L, 3, true, 1L << 40, false, 1.5, false, (double)0.1f),
            (three.TryGetInt64(out var l3), l3, three.GetInt32(), big.TryGetInt64(out var lb), lb, big.TryGetInt32(out _), half.GetDouble(), half.TryGetInt64(out _), single.GetDouble()));
        Assert.AreEqual((3.0, (double)(1L << 40)), (three.GetDouble(), big.GetDouble()));
    }

    [TestMethod]
    public void Int64_AtTheBoundariesOf2To53_ReadsInsideAndNotBeyond()
    {
        YValue max = TwoTo53;
        YValue min = -TwoTo53;
        YValue beyond = TwoTo53 * 2;
        YValue fractionNear = 4503599627370495.5; // 2^52 - 0.5, representable

        Assert.AreEqual(
            (true, 9007199254740992L, true, -9007199254740992L, false, false),
            (max.TryGetInt64(out var a), a, min.TryGetInt64(out var b), b, beyond.TryGetInt64(out _), fractionNear.TryGetInt64(out _)));
    }

    [TestMethod]
    public void Int32_OutOfRange_IsNotAnInt32ButStillAnInt64()
    {
        YValue value = 2147483648L;

        Assert.AreEqual((false, true), (value.TryGetInt32(out _), value.TryGetInt64(out _)));
        Assert.ThrowsExactly<InvalidOperationException>(() => value.GetInt32());
    }

    [TestMethod]
    public void NegativeZero_IsAnIntegerZeroEqualToZero()
    {
        YValue negative = -0.0;

        Assert.AreEqual((true, 0L, true, true), (negative.TryGetInt64(out var l), l, negative == 0, double.IsNegative(negative.GetDouble())));
        Assert.AreEqual(0.0.Equals(-0.0), negative.Equals(0.0));
    }

    [TestMethod]
    public void NaNAndInfinity_AreNumbersButNotIntegers_AndPrintAsJavaScript()
    {
        YValue nan = double.NaN;
        YValue positive = double.PositiveInfinity;
        YValue negative = double.NegativeInfinity;

        Assert.AreEqual(
            ("NaN", "Infinity", "-Infinity", false, false, false, true),
            (nan.ToString(), positive.ToString(), negative.ToString(), nan.TryGetInt64(out _), positive.TryGetInt64(out _), negative.TryGetInt64(out _), nan == double.NaN));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NaNAndInfinity_ThroughADocument_Survive(bool v2)
    {
        Assert.AreEqual(
            (true, true, true),
            (double.IsNaN(Decoded(double.NaN, v2).GetDouble()), double.IsPositiveInfinity(Decoded(double.PositiveInfinity, v2).GetDouble()), double.IsNegativeInfinity(Decoded(double.NegativeInfinity, v2).GetDouble())));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BigInteger_ThroughADocument_ReadsAsBigInteger(bool v2)
    {
        var value = Decoded(new BigInteger(-123456789012345L), v2);

        Assert.AreEqual((YValueKind.BigInteger, new BigInteger(-123456789012345L), false), (value.Kind, value.GetBigInteger(), value.TryGetInt64(out _)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Containers_ThroughADocument_ReadAsArrayAndObjectInOrder(bool v2)
    {
        var inserted = new Dictionary<string, object?> { ["z"] = 1, ["a"] = new List<object?> { "x", null, new byte[] { 7 } }, ["m"] = true };

        var value = Decoded(inserted, v2);

        Assert.AreEqual(
            (YValueKind.Object, 3, "z,a,m", 3, "x", true, (byte)7),
            (value.Kind, value.MemberCount, string.Join(',', value.EnumerateObject().Select(m => m.Key)), value["a"].Length, value["a"][0].GetString(), value["a"][1].IsNull, value["a"][2].GetBytes()[0]));
    }

    [TestMethod]
    public void ToObject_EachKind_GivesTheTypesADocumentDecodesTo()
    {
        var array = YValue.Array(1, 2.5, "s", YValue.Null);
        var obj = YValue.Object([new("k", 1)]);

        Assert.AreEqual(
            (null, true, 7, 1L << 40, 0.5, -0.0, "s", new BigInteger(9)),
            (default(YValue).ToObject(), ((YValue)true).ToObject(), ((YValue)7).ToObject(), ((YValue)(1L << 40)).ToObject(), ((YValue)0.5).ToObject(), ((YValue)(-0.0)).ToObject(), ((YValue)"s").ToObject(), ((YValue)new BigInteger(9)).ToObject()));
        Assert.AreEqual((4, 1), (((IList<object?>)array.ToObject()!).Count, ((IDictionary<string, object?>)obj.ToObject()!).Count));
    }

    [TestMethod]
    [DataRow(null, "null")]
    [DataRow(true, "true")]
    [DataRow(1.5, "1.5")]
    [DataRow(1e21, "1e+21")]
    [DataRow(1e-7, "1e-7")]
    [DataRow(123456789012345680000.0, "123456789012345680000")]
    [DataRow(-0.0, "0")]
    [DataRow("text", "text")]
    public void ToString_Scalars_IsJavaScriptString(object? scalar, string expected) =>
        Assert.AreEqual(expected, YValue.FromObject(scalar).ToString());

    [TestMethod]
    public void ToString_Containers_IsJavaScriptString()
    {
        var array = YValue.Array(1, YValue.Array(2, 3), YValue.Null, "a", true);
        var obj = YValue.Object([new("k", 1)]);

        Assert.AreEqual(("1,2,3,,a,true", "[object Object]", "1,2,3", "9007199254740993"), (array.ToString(), obj.ToString(), ((YValue)new byte[] { 1, 2, 3 }).ToString(), ((YValue)new BigInteger(9007199254740993)).ToString()));
    }

    [TestMethod]
    public void Equality_ByValue_AcrossNumberTypesAndContainerOrder()
    {
        var first = YValue.Object([new("a", YValue.Array(1, "x")), new("b", new byte[] { 1 })]);
        var reordered = YValue.Object([new("b", new byte[] { 1 }), new("a", YValue.Array(1.0, "x"))]);
        var different = YValue.Object([new("b", new byte[] { 1 }), new("a", YValue.Array(1, "y"))]);

        Assert.AreEqual((true, true, false, false), (first == reordered, first.GetHashCode() == reordered.GetHashCode(), first == different, ((YValue)1).Equals((YValue)"1")));
        Assert.AreEqual((false, false), ((YValue)1 == new BigInteger(1), YValue.Array() == YValue.Object([])));
    }

    [TestMethod]
    public void Equality_SharedTypes_ByReference()
    {
        var map = new YMap();

        Assert.AreEqual((true, false), ((YValue)map == map, (YValue)map == new YMap()));
    }

    [TestMethod]
    public void FromObject_ClrContainers_AreCopied()
    {
        var list = new List<object?> { 1, new List<object?> { "x" } };

        var value = YValue.FromObject(list);
        ((List<object?>)list[1]!).Add("y");
        list.Add(3);

        Assert.AreEqual((2, 1), (value.Length, value[1].Length));
    }

    [TestMethod]
    public void FromObject_YValue_IsTheSameValue()
    {
        var value = YValue.Array(1);

        Assert.AreEqual(value, YValue.FromObject(value));
    }

    [TestMethod]
    public void FromObject_NumbersOfEveryBuiltInType_AreNumbers()
    {
        object[] numbers = [(sbyte)1, (byte)1, (short)1, (ushort)1, 1, 1u, 1L, 1ul, 1f, 1.0];

        Assert.IsTrue(numbers.All(n => YValue.FromObject(n) == 1));
    }

    [TestMethod]
    public void FromObject_UnsupportedTypes_ThrowArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(DateTime.UnixEpoch));
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(new object()));
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(new List<object?> { 1, Guid.Empty }));
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(1m));
    }

    [TestMethod]
    public void FromObject_OutOfRangeNumbers_ThrowArgumentOutOfRange()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => YValue.FromObject(9007199254740992L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => YValue.FromObject(ulong.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => YValue.FromObject(BigInteger.Pow(2, 64)));
    }

    [TestMethod]
    public void FromObject_ValueThatContainsItself_Throws()
    {
        var list = new List<object?>();
        list.Add(list);

        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(list));
    }

    [TestMethod]
    public void FromObject_NonStringKeys_Throw()
    {
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(new System.Collections.Hashtable { [1] = 2 }));
    }

    [TestMethod]
    public void FromObject_SharedTypeAndDocument_AreKept()
    {
        var map = new YMap();
        var doc = new YDoc();

        Assert.AreEqual((true, true), (YValue.FromObject(map).TryGetShared(out var t) && ReferenceEquals(t, map), YValue.FromObject(doc).TryGetDocument(out var d) && ReferenceEquals(d, doc)));
    }

    [TestMethod]
    public void Array_WithASharedType_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => YValue.Array(1, new YMap()));
        Assert.ThrowsExactly<ArgumentException>(() => YValue.Object([new("k", new YDoc())]));
        Assert.ThrowsExactly<ArgumentException>(() => YValue.FromObject(new List<object?> { new YMap() }));
    }

    [TestMethod]
    public void Object_RepeatedKey_KeepsFirstPlaceAndLastValue()
    {
        var value = YValue.Object([new("a", 1), new("b", 2), new("a", 3)]);

        Assert.AreEqual(("a=3,b=2", 2), (string.Join(',', value.EnumerateObject().Select(m => $"{m.Key}={m.Value}")), value.MemberCount));
    }

    [TestMethod]
    public void Accessors_WrongKind_ThrowInvalidOperation()
    {
        YValue text = "s";

        Assert.ThrowsExactly<InvalidOperationException>(() => text.GetDouble());
        Assert.ThrowsExactly<InvalidOperationException>(() => text.GetBoolean());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = text.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = text["k"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = (int)text);
    }

    [TestMethod]
    public void Indexers_MissingMemberOrIndex_Throw()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() => _ = YValue.Object([])["k"]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = YValue.Array(1)[1]);
        Assert.AreEqual(false, YValue.Object([]).TryGetMember("k", out _));
    }

    [TestMethod]
    public void ExplicitCasts_MatchingKind_Convert()
    {
        Assert.AreEqual(
            (true, 3, 4L, 2.5, "s", new BigInteger(5), 1),
            ((bool)(YValue)true, (int)(YValue)3, (long)(YValue)4, (double)(YValue)2.5, (string)(YValue)"s", (BigInteger)(YValue)new BigInteger(5), ((byte[])(YValue)new byte[] { 1 })[0]));
    }

    [TestMethod]
    public void EnumerateArray_Values_InOrder()
    {
        var value = YValue.Array(3, "b", YValue.Null);

        CollectionAssert.AreEqual(new[] { YValueKind.Number, YValueKind.String, YValueKind.Null }, value.EnumerateArray().Select(v => v.Kind).ToArray());
    }

    [TestMethod]
    public void TryGetBytes_Bytes_ViewsWithoutCopyingForTheCaller()
    {
        YValue value = new byte[] { 4, 5 };

        Assert.AreEqual((true, 2, 5), (value.TryGetBytes(out var memory), memory.Length, (int)memory.Span[1]));
        Assert.AreEqual(false, ((YValue)1).TryGetBytes(out _));
    }
}
