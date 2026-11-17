using System.Collections.Generic;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The kind-checked reads <c>TryGetDouble</c>, <c>TryGetBoolean</c> and <c>TryGetBigInteger</c> of <see cref="YValue"/>.</summary>
[TestClass]
public class YValueTryGetTests
{
    private static YValue Sample(string kind) => kind switch
    {
        "null" => YValue.Null,
        "true" => YValue.True,
        "false" => YValue.False,
        "double" => 1.5,
        "int" => 7,
        "long" => 9_000_000_000L,
        "big" => new BigInteger(-123456789012345L),
        "string" => "text",
        "bytes" => new byte[] { 1, 2 },
        "array" => YValue.Array([1, 2]),
        "object" => YValue.Object([("k", (YValue)1)]),
        "type" => new YMap(),
        "doc" => new YDoc(),
        _ => throw new KeyNotFoundException(kind),
    };

    public static IEnumerable<object[]> NotNumbers => Of("null", "true", "false", "big", "string", "bytes", "array", "object", "type", "doc");

    public static IEnumerable<object[]> NotBooleans => Of("null", "string", "bytes", "array", "object", "type", "doc");

    public static IEnumerable<object[]> NotBigIntegers => Of("null", "true", "false", "double", "int", "long", "string", "bytes", "array", "object", "type", "doc");

    private static IEnumerable<object[]> Of(params string[] kinds)
    {
        foreach (var kind in kinds)
        {
            yield return [kind];
        }
    }

    [TestMethod]
    [DataRow("double", 1.5)]
    [DataRow("int", 7.0)]
    [DataRow("long", 9_000_000_000.0)]
    public void TryGetDouble_Number_ReturnsTrueAndTheValue(string kind, double expected)
    {
        var ok = Sample(kind).TryGetDouble(out var value);

        Assert.AreEqual((true, expected), (ok, value));
    }

    [TestMethod]
    public void TryGetDouble_LargestSafeInteger_ReturnsItExactly()
    {
        YValue max = 9007199254740991L;

        var ok = max.TryGetDouble(out var value);

        Assert.AreEqual((true, 9007199254740991.0), (ok, value));
    }

    [TestMethod]
    [DynamicData(nameof(NotNumbers))]
    public void TryGetDouble_OtherKind_ReturnsFalseAndDefault(string kind)
    {
        var ok = Sample(kind).TryGetDouble(out var value);

        Assert.AreEqual((false, 0.0), (ok, value));
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    public void TryGetBoolean_Boolean_ReturnsTrueAndTheValue(string kind, bool expected)
    {
        var ok = Sample(kind).TryGetBoolean(out var value);

        Assert.AreEqual((true, expected), (ok, value));
    }

    [TestMethod]
    [DynamicData(nameof(NotBooleans))]
    public void TryGetBoolean_OtherKind_ReturnsFalseAndDefault(string kind)
    {
        var ok = Sample(kind).TryGetBoolean(out var value);

        Assert.AreEqual((false, false), (ok, value));
    }

    [TestMethod]
    [DataRow("double")]
    [DataRow("int")]
    [DataRow("long")]
    [DataRow("big")]
    public void TryGetBoolean_NumberKind_ReturnsFalseAndDefault(string kind)
    {
        var ok = Sample(kind).TryGetBoolean(out var value);

        Assert.AreEqual((false, false), (ok, value));
    }

    [TestMethod]
    public void TryGetBigInteger_BigInteger_ReturnsTrueAndTheValue()
    {
        var ok = Sample("big").TryGetBigInteger(out var value);

        Assert.AreEqual((true, new BigInteger(-123456789012345L)), (ok, value));
    }

    [TestMethod]
    public void TryGetBigInteger_LongConvertedToBigInteger_ReturnsTrueAndTheValue()
    {
        YValue value = new BigInteger(9_000_000_000L);

        var ok = value.TryGetBigInteger(out var read);

        Assert.AreEqual((true, new BigInteger(9_000_000_000L)), (ok, read));
    }

    [TestMethod]
    public void TryGetBigInteger_LongValue_IsANumberNotABigInteger()
    {
        var ok = Sample("long").TryGetBigInteger(out var value);

        Assert.AreEqual((false, BigInteger.Zero), (ok, value));
    }

    [TestMethod]
    [DynamicData(nameof(NotBigIntegers))]
    public void TryGetBigInteger_OtherKind_ReturnsFalseAndDefault(string kind)
    {
        var ok = Sample(kind).TryGetBigInteger(out var value);

        Assert.AreEqual((false, BigInteger.Zero), (ok, value));
    }
}
