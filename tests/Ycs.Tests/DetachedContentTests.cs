using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class DetachedContentTests
{
    [TestMethod]
    public void Clone_TextWithSharedEmbed_ClonesTheEmbed()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        text.InsertEmbed(1, new YMap { ["x"] = 1 });

        var clone = text.Clone();
        doc.GetMap("m")["c"] = clone;

        var embedded = clone.ToDelta().OfType<Delta.Insert>().Select(i => i.Value).Where(v => v.TryGetSharedType<YMap>(out _)).Single();
        Assert.AreEqual(("ab", true, 3), (clone.ToString(), !ReferenceEquals(embedded.ToObject(), text.ToDelta()[1] is Delta.Insert o ? o.Value.ToObject() : null), clone.Length));
    }

    [TestMethod]
    public void Clone_DetachedTextWithSharedEmbed_KeepsTheEmbedForTheDocument()
    {
        var text = new YText("ab");
        text.InsertEmbed(1, new YMap { ["x"] = 1 });
        var clone = text.Clone();
        var doc = new YDoc();

        doc.GetMap("m")["c"] = clone;

        var embed = ((Delta.Insert)((YText)doc.GetMap("m")["c"].ToObject()!).ToDelta()[1]).Value;
        Assert.AreEqual(1, (int)((YMap)embed.ToObject()!)["x"]);
    }

    [TestMethod]
    public void Clone_XmlTextWithSharedEmbed_ClonesTheEmbed()
    {
        var doc = new YDoc();
        var fragment = doc.GetXmlFragment("f");
        var text = new YXmlText("ab");
        fragment.Add(text);
        text.InsertEmbed(1, new YMap { ["x"] = 1 });

        var clone = (YXmlText)text.Clone();
        fragment.Add(clone);

        Assert.AreEqual(3, clone.Length);
    }

    [TestMethod]
    public void Insert_DetachedTextWithAnEmbedFromADocument_ThrowsAndChangesNothing()
    {
        var doc = new YDoc();
        var owned = new YMap { ["x"] = 1 };
        doc.GetMap("owner")["m"] = owned;
        var text = new YText("xyz");
        text.InsertEmbed(1, owned);
        text.Insert(4, "tail");
        var target = doc.GetMap("target");

        Assert.ThrowsExactly<ArgumentException>(() => target["t"] = text);

        Assert.AreEqual(0, target.Count);
    }

    [TestMethod]
    public void Insert_DetachedSubtreeBeyondMaxDepth_ThrowsBeforeIntegratingAnything()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 10 } });
        var map = doc.GetMap("m");
        var deep = new YMap();
        var level = deep;
        for (var i = 0; i < 12; i++)
        {
            var next = new YMap();
            level["n"] = next;
            level = next;
        }

        var wide = new YMap { ["x"] = 1, ["deep"] = deep, ["y"] = 2 };

        Assert.ThrowsExactly<InvalidOperationException>(() => map["deepMap"] = wide);

        Assert.AreEqual((0, false, 0), (map.Count, map.ContainsKey("deepMap"), doc.GetArray("never").Count));
    }

    [TestMethod]
    public void Insert_DetachedArrayWithADeepMap_ThrowsBeforeIntegratingAnything()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 10 } });
        var map = doc.GetMap("m");
        var deepMap = new YMap();
        var level = deepMap;
        for (var i = 0; i < 12; i++)
        {
            var next = new YMap();
            level["n"] = next;
            level = next;
        }

        var updates = 0;
        doc.Updated += (_, _) => updates++;

        Assert.ThrowsExactly<InvalidOperationException>(() => map["k"] = new YArray("x", deepMap, "y"));

        Assert.AreEqual((false, 0), (map.ContainsKey("k"), updates));
    }

    [TestMethod]
    public void Insert_DetachedTextWithADeepEmbed_ThrowsBeforeIntegratingAnything()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 4 } });
        var deep = new YMap { ["a"] = new YMap { ["b"] = new YMap { ["c"] = new YMap() } } };
        var text = new YText("ab");
        text.InsertEmbed(1, deep);

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetMap("m")["t"] = text);

        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void InsertEmbed_Document_ThrowsArgumentException()
    {
        var text = new YDoc().GetText();

        var e = Assert.ThrowsExactly<ArgumentException>(() => text.InsertEmbed(0, new YDoc()));

        StringAssert.Contains(e.Message, "YArray or a YMap");
    }

    [TestMethod]
    public void InsertEmbed_DocumentInDetachedText_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new YText().InsertEmbed(0, new YDoc()));
    }

    [TestMethod]
    public void ApplyDelta_InsertOfADocument_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new YText().ApplyDelta([new Delta.Insert(new YDoc())]));
    }

    [TestMethod]
    public void Substring_TextWithAnEmbed_IndexesAsLengthDoes()
    {
        var text = new YDoc().GetText();
        text.Insert(0, "abcdef");
        text.InsertEmbed(3, new YMap());

        Assert.AreEqual((7, "abcdef", "bc", "cd", "d", "", "ef"), (text.Length, text.Substring(0, text.Length), text.Substring(1, 2), text.Substring(2, 3), text.Substring(4, 1), text.Substring(3, 1), text.Substring(5, 2)));
    }

    [TestMethod]
    public void Substring_DetachedTextWithAnEmbed_IndexesAsLengthDoes()
    {
        var text = new YText("abcdef");
        text.InsertEmbed(3, 7);

        Assert.AreEqual(("abcdef", "de"), (text.Substring(0, text.Length), text.Substring(4, 2)));
    }

    [TestMethod]
    public void Substring_BeyondLength_Throws()
    {
        var text = new YDoc().GetText();
        text.Insert(0, "ab");
        text.InsertEmbed(1, 1);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => text.Substring(1, 3));
    }

    [TestMethod]
    public void ToString_Snapshot_ReadsTheTextThen()
    {
        var doc = new YDoc();
        var text = doc.GetText();
        text.Insert(0, "abc");
        var snapshot = doc.CreateSnapshot();
        text.InsertEmbed(1, 5);
        text.Insert(0, "xy");

        Assert.AreEqual(("abc", "xyabc"), (text.ToString(snapshot), text.ToString()));
    }

    [TestMethod]
    public void GetAttributes_Snapshot_ReadsTheAttributesThen()
    {
        var doc = new YDoc();
        var text = doc.GetText();
        text.SetAttribute("a", 1);
        var snapshot = doc.CreateSnapshot();
        text.SetAttribute("b", 2);

        Assert.AreEqual((1, 2), (text.GetAttributes(snapshot).Count, text.GetAttributes().Count));
    }

    [TestMethod]
    public void AddRange_AndInsertRange_TakeAnyCollection()
    {
        var fragment = new YDoc().GetXmlFragment("f");

        fragment.AddRange(new[] { new YXmlText("c") }.AsEnumerable());
        fragment.InsertRange(0, new YType[] { new YXmlText("a"), new YXmlText("b") }.Where(_ => true));

        Assert.AreEqual(("ab", "c"), (fragment[0].ToString() + fragment[1], fragment[2].ToString()));
    }
}
