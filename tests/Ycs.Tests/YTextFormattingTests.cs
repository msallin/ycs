using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Local formatting as Yjs 13.6.32 does it (formatText, insertNegatedAttributes, equalAttrs); the
/// expected encodings were written by Yjs running the same steps with client id 1.
/// </summary>
[TestClass]
public class YTextFormattingTests
{
    [TestMethod]
    public void Format_BeyondTheEnd_AppendsOneNewlinePerMissingCharacter()
    {
        var text = NewText();
        text.Insert(0, "ab");

        text.Format(1, 3, new Dictionary<string, YValue> { ["bold"] = true });

        Assert.AreEqual("ab\n\n", text.ToString());
    }

    [TestMethod]
    public void ApplyDelta_PlainInsertAfterAFormattedOne_GoesAfterTheNegation()
    {
        var text = NewText();

        text.ApplyDelta(new List<Delta>
        {
            new Delta.Insert("a") { Attributes = new Dictionary<string, YValue> { ["bold"] = true } },
            new Delta.Insert("b"),
        });

        Assert.AreEqual("AQQBAAYBAXQEYm9sZAR0cnVlhAEAAWGGAQEEYm9sZARudWxshAECAWIA", Convert.ToBase64String(text.Doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Format_SameNumberOfAnotherClrType_ChangesNothing()
    {
        var text = NewText();
        text.Insert(0, "abc");
        text.Format(0, 3, new Dictionary<string, YValue> { ["size"] = 1 });

        text.Format(1, 1, new Dictionary<string, YValue> { ["size"] = 1.0 });

        Assert.AreEqual("AQMBAAQBAXQDYWJjRgEABHNpemUBMYYBAgRzaXplBG51bGwA", Convert.ToBase64String(text.Doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void EqualAttrs_FlatMapsAndLists_CompareTheirValuesWithStrictEquality()
    {
        var map = new Dictionary<string, object> { ["a"] = 1L, ["b"] = "x" };
        var sameMap = new Dictionary<string, object> { ["b"] = "x", ["a"] = 1 };
        var nested = new Dictionary<string, object> { ["a"] = new List<object> { 1 } };
        var sameNested = new Dictionary<string, object> { ["a"] = new List<object> { 1 } };

        Assert.AreEqual(
            "True|True|False|False",
            $"{YText.EqualAttrs(map, sameMap)}|{YText.EqualAttrs(new List<object> { 1, "x" }, new List<object> { 1.0, "x" })}|" +
            $"{YText.EqualAttrs(nested, sameNested)}|{YText.EqualAttrs(true, "true")}");
    }

    private static YText NewText() => new YDoc(new YDocOptions { ClientId = 1 }).GetText("t");
}
