using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YValueJsonTests
{
    [TestMethod]
    public void ToJsonString_EveryKindOfSharedType_IsTheYjsToJson()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["text"] = new YText("hi");
        map["array"] = new YArray(1, "a");
        map["map"] = new YMap { ["k"] = true };
        map["n"] = YValue.Null;
        var xml = doc.GetXmlFragment("x");
        var p = new YXmlElement("p");
        xml.Add(p);
        p.SetAttribute("a", "1 < 2");
        p.Add(new YXmlText("t"));

        Assert.AreEqual(
            (@"{""text"":""hi"",""array"":[1,""a""],""map"":{""k"":true},""n"":null}", "\"<p a=\\\"1 < 2\\\">t</p>\"", "\"hi\"", "[1,\"a\"]"),
            (map.ToJsonString(), xml.ToJsonString(), doc.GetMap("m")["text"].ToJsonString(), doc.GetMap("m")["array"].ToJsonString()));
    }

    [TestMethod]
    public void ToJsonString_Numbers_UseJavaScriptFormatting()
    {
        var value = YValue.Array(1.0, -0.0, 1.5, 1e21, 1.5e-7, 123456789012345680000.0, 9007199254740993.0, double.NaN, double.PositiveInfinity, -3);

        Assert.AreEqual("[1,0,1.5,1e+21,1.5e-7,123456789012345680000,9007199254740992,null,null,-3]", value.ToJsonString());
    }

    [TestMethod]
    public void ToJsonString_BytesBigIntegerAndSubdocument_HaveTheirDocumentedForms()
    {
        var doc = new YDoc();
        var sub = new YDoc();
        sub.GetMap("inner")["k"] = 1;
        sub.GetText("t").Insert(0, "x");
        doc.GetMap("m")["sub"] = sub;
        doc.GetMap("m")["bytes"] = new byte[] { 1, 2, 3, 250 };
        doc.GetMap("m")["big"] = new BigInteger(long.MinValue);

        Assert.AreEqual(@"{""sub"":{""inner"":{""k"":1},""t"":""x""},""bytes"":""AQID+g=="",""big"":-9223372036854775808}", doc.GetMap("m").ToJsonString());
    }

    [TestMethod]
    public void ToJsonString_UnsetRootInASubdocument_IsLeftOut()
    {
        var doc = new YDoc();
        var sub = new YDoc();
        sub.ApplyUpdate(MakeUpdate("a", "later"));
        doc.GetArray("a").Add(sub);

        Assert.AreEqual("[{}]", doc.GetArray("a").ToJsonString());
    }

    [TestMethod]
    public void ToJsonString_StringsWithSpecialCharacters_AreEscapedAsJson()
    {
        var value = YValue.Object(("k\"", "é \u2028 <>&\n\ud83d\ude00"), ("lone", "a\ud83db"), ("a\ud83d", 1));

        Assert.AreEqual("{\"k\\\"\":\"é \\u2028 <>&\\n\\uD83D\\uDE00\",\"lone\":\"a\\ud83db\",\"a\ufffd\":1}", value.ToJsonString());
    }

    [TestMethod]
    public void ToJsonString_Options_AreHonoured()
    {
        var value = YValue.Object(("é", YValue.Array(1)));

        Assert.AreEqual(
            ("{\"\\u00E9\":[1]}", "{\r\n  \"é\": [\r\n    1\r\n  ]\r\n}".Replace("\r\n", Environment.NewLine)),
            (value.ToJsonString(new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }), value.ToJsonString(new JsonWriterOptions { Indented = true })));
    }

    [TestMethod]
    public void WriteTo_AWriter_WritesTheValueAsAMember()
    {
        var doc = new YDoc();
        doc.GetArray("a").Add(1, 2);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("arr");
            doc.GetArray("a").WriteTo(writer);
            writer.WritePropertyName("v");
            YValue.Object(("x", 1.5)).WriteTo(writer);
            writer.WriteEndObject();
        }

        Assert.AreEqual(@"{""arr"":[1,2],""v"":{""x"":1.5}}", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [TestMethod]
    public void WriteTo_NullWriter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => YValue.Null.WriteTo(null!));
        Assert.Throws<ArgumentNullException>(() => new YDoc().GetMap("m").WriteTo(null!));
    }

    [TestMethod]
    public void ToJsonString_ASharedTypeValue_IsTheTypesJson()
    {
        var map = new YMap { ["k"] = 1 };

        Assert.AreEqual(@"{""k"":1}", ((YValue)map).ToJsonString());
    }

    [TestMethod]
    public void ToJsonString_NestedAsDeepAsTheLimitAllows_DoesNotOverflowTheStack()
    {
        var doc = new YDoc();
        var current = doc.GetArray("a");
        for (var i = 0; i < 2_000; i++)
        {
            var next = new YArray();
            current.Add(next);
            current = next;
        }

        Assert.AreEqual(2_001 * 2 + 0, doc.GetArray("a").ToJsonString().Length);
    }

    [TestMethod]
    public void Parse_AJsonText_IsTheValueAndRoundTrips()
    {
        var json = "{\"a\":[1,2.5,\"s\",null,true,{\"b\":false}],\"c\":9007199254740993,\"d\":\"\\ud83d\"}";

        var value = YValue.Parse(json);

        Assert.AreEqual(
            (YValueKind.Object, 1, 2.5, 3, 9007199254740992.0, "\ud83d", "{\"a\":[1,2.5,\"s\",null,true,{\"b\":false}],\"c\":9007199254740992,\"d\":\"\\ud83d\"}"),
            (value.Kind, value["a"][0].GetInt32(), value["a"][1].GetDouble(), value["a"].Length - 3, value["c"].GetDouble(), value["d"].GetString(), value.ToJsonString()));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("[1,]")]
    [DataRow("1 2")]
    [DataRow("nope")]
    public void Parse_NotOneJsonValue_ThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => YValue.Parse(json));
    }

    [TestMethod]
    public void Parse_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => YValue.Parse(null!));
    }

    [TestMethod]
    public void Parse_TooDeep_ThrowsJsonException()
    {
        var json = new string('[', UpdateLimits.DefaultMaxDepth + 2) + new string(']', UpdateLimits.DefaultMaxDepth + 2);

        Assert.Throws<JsonException>(() => YValue.Parse(json));
    }

    [TestMethod]
    public void FromJson_AnElement_IsTheParsedValue()
    {
        var element = JsonElement.Parse("[1,{\"k\":\"v\"}]");

        Assert.AreEqual(YValue.Array(1, YValue.Object(("k", "v"))), YValue.FromJson(element));
    }

    [TestMethod]
    public void FromJson_ADefaultElement_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => YValue.FromJson(default(JsonElement)));
    }

    [TestMethod]
    public void FromJson_ANode_IsTheParsedValue()
    {
        JsonNode node = new JsonObject { ["a"] = new JsonArray(1, 2.5, "x", null), ["b"] = true };

        Assert.AreEqual(
            (YValue.Object(("a", YValue.Array(1, 2.5, "x", YValue.Null)), ("b", true)), YValue.Null),
            (YValue.FromJson(node), YValue.FromJson((JsonNode?)null)));
    }

    [TestMethod]
    public void Object_TupleMembers_AreAnObject()
    {
        var obj = YValue.Object(("k", 1), ("s", "v"), ("a", YValue.Array(1)));

        Assert.AreEqual(@"{""k"":1,""s"":""v"",""a"":[1]}", obj.ToJsonString());
    }

    [TestMethod]
    public void Object_TupleMembers_RepeatedKeyKeepsFirstPlaceAndLastValue()
    {
        Assert.AreEqual(@"{""a"":3,""b"":2}", YValue.Object(("a", 1), ("b", 2), ("a", 3)).ToJsonString());
    }

    [TestMethod]
    public void Object_TupleMembers_NullKeyOrSharedTypeThrows()
    {
        Assert.AreEqual(
            (typeof(ArgumentNullException), typeof(ArgumentException)),
            (Catch(() => YValue.Object((null!, 1))), Catch(() => YValue.Object(("k", new YMap())))));
    }

    [TestMethod]
    public void Object_NoMembers_IsTheEmptyObject()
    {
        Assert.AreEqual("{}", YValue.Object().ToJsonString());
    }

    private static Type? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e.GetType();
        }
    }

    private static byte[] MakeUpdate(string root, string text)
    {
        var doc = new YDoc();
        doc.GetText(root).Insert(0, text);
        return doc.EncodeStateAsUpdate();
    }
}
