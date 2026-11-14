// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="YJson"/> against the output of Node's <c>JSON.stringify</c> for the same values (each expected
/// string below was produced by Node 24, not derived from the implementation).
/// </summary>
[TestClass]
public class YJsonTests
{
    [DataTestMethod]
    [DataRow(1.0, "1")]
    [DataRow(1.5, "1.5")]
    [DataRow(-2.25, "-2.25")]
    [DataRow(0.1, "0.1")]
    [DataRow(0.000001, "0.000001")]
    [DataRow(1e-7, "1e-7")]
    [DataRow(1.5e-7, "1.5e-7")]
    [DataRow(123456789012345680000.0, "123456789012345680000")]
    [DataRow(1e21, "1e+21")]
    [DataRow(1.7976931348623157e308, "1.7976931348623157e+308")]
    [DataRow(5e-324, "5e-324")]
    [DataRow(-0.0, "0")]
    [DataRow(100.0, "100")]
    [DataRow(0.3333333333333333, "0.3333333333333333")]
    [DataRow(1234.5678, "1234.5678")]
    [DataRow(9007199254740992.0, "9007199254740992")]
    [DataRow(double.NaN, "null")]
    [DataRow(double.PositiveInfinity, "null")]
    public void DoublesAreWrittenAsJavaScriptWritesThem(double value, string expected)
    {
        Assert.AreEqual(expected, YJson.Stringify(value));
    }

    [TestMethod]
    public void StringsEscapeOnlyWhatJsonStringifyEscapes()
    {
        var input = "a\"b\\c\n\t\u0001\u001f é 😀 \ud800 \u2028";
        Assert.AreEqual("\"a\\\"b\\\\c\\n\\t\\u0001\\u001f é 😀 \\ud800 \u2028\"", YJson.Stringify(input));
    }

    [TestMethod]
    public void ObjectsAndArraysAreCompactAndKeepInsertionOrder()
    {
        var value = new Dictionary<string, object>
        {
            { "b", true },
            { "n", null },
            { "a", new List<object> { 1L, 2.5, "x" } },
            { "o", new Dictionary<string, object> { { "k", 1 } } },
        };
        Assert.AreEqual("{\"b\":true,\"n\":null,\"a\":[1,2.5,\"x\"],\"o\":{\"k\":1}}", YJson.Stringify(value));
    }

    [TestMethod]
    public void ParseReturnsPlainClrValues()
    {
        var parsed = (Dictionary<string, object>)YJson.Parse("{\"i\":42,\"d\":1.5,\"e\":1e3,\"s\":\"2020-01-01T00:00:00Z\",\"a\":[true,null],\"o\":{}}");

        Assert.AreEqual(42L, parsed["i"]);
        Assert.AreEqual(1.5, parsed["d"]);
        Assert.AreEqual(1000.0, parsed["e"]);
        // An ISO-8601-looking string stays a string, as JSON.parse keeps it.
        Assert.AreEqual("2020-01-01T00:00:00Z", parsed["s"]);
        CollectionAssert.AreEqual(new List<object> { true, null }, (List<object>)parsed["a"]);
        Assert.AreEqual(0, ((Dictionary<string, object>)parsed["o"]).Count);
    }

    [TestMethod]
    public void ParseThenStringifyRoundTripsTheText()
    {
        const string json = "{\"bold\":true,\"color\":\"#ff0000\",\"size\":12,\"ratio\":0.5,\"tags\":[\"a\",\"b\"]}";
        Assert.AreEqual(json, YJson.Stringify(YJson.Parse(json)));
    }
}
