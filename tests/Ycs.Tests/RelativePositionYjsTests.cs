using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Relative positions as Yjs 13.6.32 creates them, and JSON positions from untrusted peers.</summary>
[TestClass]
public class RelativePositionYjsTests
{
    // The array [x, y, <YText "abc">] of client 1: the text's item is 1:2, its content 1:3-1:5.
    private static (YDoc Doc, YArray Array, YText Nested) NestedText()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("arr");
        array.Insert(0, "x", "y");
        var nested = new YText();
        array.Insert(2, nested);
        nested.Insert(0, "abc");
        return (doc, array, nested);
    }

    [TestMethod]
    [DataRow(3, 0, "02010200", 3, DisplayName = "end")]
    [DataRow(0, -1, "02010241", 0, DisplayName = "start, left-associated")]
    public void FromTypeIndex_EdgeOfNestedText_NamesTheTextAndResolvesIntoIt(int index, int assoc, string yjsHex, int resolvedIndex)
    {
        var (doc, _, nested) = NestedText();

        var rpos = RelativePosition.FromIndex(nested, index, assoc);
        Assert.IsTrue(rpos.TryResolve(doc, out var abs));

        Assert.AreEqual((yjsHex, true, resolvedIndex), (Convert.ToHexStringLower(rpos.Encode()), abs.Type == nested, abs.Index));
    }

    [TestMethod]
    public void FromTypeIndex_EmptyNestedText_NamesTheText()
    {
        var (_, array, _) = NestedText();
        var empty = new YText();
        array.Insert(3, empty);

        Assert.AreEqual("02010600", Convert.ToHexStringLower(RelativePosition.FromIndex(empty, 0).Encode()));
    }

    [TestMethod]
    [DataRow("""{"item":"1:2"}""", DisplayName = "item not an object")]
    [DataRow("""{"type":5}""", DisplayName = "type not an object")]
    [DataRow("""{"item":{"clock":1}}""", DisplayName = "client missing")]
    [DataRow("""{"type":{"client":1}}""", DisplayName = "clock missing")]
    [DataRow("""{"item":{"client":1,"clock":-1}}""", DisplayName = "clock negative")]
    [DataRow("""{"item":{"client":1e300,"clock":0}}""", DisplayName = "client too large")]
    [DataRow("""{"item":{"client":1,"clock":0.5}}""", DisplayName = "clock a fraction")]
    [DataRow("""{"item":{"client":1,"clock":"0"}}""", DisplayName = "clock a string")]
    [DataRow("""{"tname":"t","assoc":"x"}""", DisplayName = "assoc a string")]
    [DataRow("""{"tname":"t","assoc":1e300}""", DisplayName = "assoc too large")]
    [DataRow("""{"tname":1}""", DisplayName = "tname a number")]
    [DataRow("""{"assoc":0}""", DisplayName = "nothing named")]
    [DataRow("""{"item":null,"tname":null}""", DisplayName = "null members are absent")]
    [DataRow("""[]""", DisplayName = "not an object")]
    public void FromJson_Malformed_ThrowsYUpdateException(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.FromJson(document.RootElement));
    }

    [TestMethod]
    public void FromJson_AssocBeyondInt_IsClamped()
    {
        using var document = JsonDocument.Parse("""{"tname":"t","assoc":-1e15}""");

        Assert.AreEqual(int.MinValue, RelativePosition.FromJson(document.RootElement).Assoc);
    }

    [TestMethod]
    public void TryResolve_JsonClockBeyondTheDocument_IsFalse()
    {
        var (doc, _, _) = NestedText();
        using var document = JsonDocument.Parse("""{"item":{"client":1,"clock":1099511627776}}""");
        var rpos = RelativePosition.FromJson(document.RootElement);

        Assert.IsFalse(rpos.TryResolve(doc, out _));
    }
}
