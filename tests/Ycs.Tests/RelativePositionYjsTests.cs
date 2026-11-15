using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Relative positions as Yjs 13.6.32 creates them.</summary>
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
}
