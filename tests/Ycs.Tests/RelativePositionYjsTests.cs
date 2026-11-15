using System;
using System.Collections.Generic;
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
        array.Insert(0, new object[] { "x", "y" });
        var nested = new YText();
        array.Insert(2, new object[] { nested });
        nested.Insert(0, "abc");
        return (doc, array, nested);
    }

    [TestMethod]
    [DataRow(3, 0, "02010200", 3, DisplayName = "end")]
    [DataRow(0, -1, "02010241", 0, DisplayName = "start, left-associated")]
    public void FromTypeIndex_EdgeOfNestedText_NamesTheTextAndResolvesIntoIt(int index, int assoc, string yjsHex, int resolvedIndex)
    {
        var (doc, _, nested) = NestedText();

        var rpos = RelativePosition.FromTypeIndex(nested, index, assoc);
        var abs = AbsolutePosition.TryCreateFromRelativePosition(rpos, doc);

        Assert.AreEqual((yjsHex, true, resolvedIndex), (Convert.ToHexStringLower(rpos.ToArray()), abs.Type == nested, abs.Index));
    }

    [TestMethod]
    public void FromTypeIndex_EmptyNestedText_NamesTheText()
    {
        var (_, array, _) = NestedText();
        var empty = new YText();
        array.Insert(3, new object[] { empty });

        Assert.AreEqual("02010600", Convert.ToHexStringLower(RelativePosition.FromTypeIndex(empty, 0).ToArray()));
    }

    public static IEnumerable<object[]> MalformedJson => new[]
    {
        new object[] { "item not an object", new Dictionary<string, object> { ["item"] = "1:2" } },
        new object[] { "type not an object", new Dictionary<string, object> { ["type"] = 5 } },
        new object[] { "client missing", new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["clock"] = 1 } } },
        new object[] { "clock missing", new Dictionary<string, object> { ["type"] = new Dictionary<string, object> { ["client"] = 1 } } },
        new object[] { "clock negative", new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["client"] = 1, ["clock"] = -1 } } },
        new object[] { "client too large", new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["client"] = 1e300, ["clock"] = 0 } } },
        new object[] { "clock a fraction", new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["client"] = 1, ["clock"] = 0.5 } } },
        new object[] { "clock a string", new Dictionary<string, object> { ["item"] = new Dictionary<string, object> { ["client"] = 1, ["clock"] = "0" } } },
        new object[] { "assoc a string", new Dictionary<string, object> { ["tname"] = "t", ["assoc"] = "x" } },
        new object[] { "tname a number", new Dictionary<string, object> { ["tname"] = 1 } },
        new object[] { "nothing named", new Dictionary<string, object> { ["assoc"] = 0 } },
    };

    [TestMethod]
    [DynamicData(nameof(MalformedJson))]
    public void CreateRelativePositionFromJSON_Malformed_ThrowsYUpdateException(string name, Dictionary<string, object> json)
    {
        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.CreateRelativePositionFromJSON(json), name);
    }

    [TestMethod]
    public void CreateRelativePositionFromJSON_AssocBeyondInt_IsClamped()
    {
        var rpos = RelativePosition.CreateRelativePositionFromJSON(new Dictionary<string, object> { ["tname"] = "t", ["assoc"] = -1e15 });

        Assert.AreEqual(int.MinValue, rpos.Assoc);
    }

    [TestMethod]
    public void CreateAbsolutePosition_JsonClockBeyondTheDocument_IsNull()
    {
        var (doc, _, _) = NestedText();
        var rpos = RelativePosition.CreateRelativePositionFromJSON(new Dictionary<string, object>
        {
            ["item"] = new Dictionary<string, object> { ["client"] = 1L, ["clock"] = (double)(1L << 40) },
        });

        Assert.IsNull(AbsolutePosition.TryCreateFromRelativePosition(rpos, doc));
    }
}
