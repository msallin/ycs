using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YTextUsabilityTests
{
    [TestMethod]
    public void Insert_CollectionExpressionAttributes_FormatsTheText()
    {
        var text = new YDoc().GetText();

        text.Insert(0, "hi", [new("bold", true), new("size", 2)]);

        var op = (Delta.Insert)text.ToDelta().Single();
        Assert.AreEqual((true, 2), ((bool)op.Attributes["bold"], (int)op.Attributes["size"]));
    }

    [TestMethod]
    public void Format_PairAndDictionaryConversions_Format()
    {
        var text = new YDoc().GetText();
        text.Insert(0, "abcd");

        text.Format(0, 2, ("bold", true));
        text.Format(2, 2, new Dictionary<string, YValue> { ["em"] = true });

        Assert.AreEqual(("bold", "em"), (Names(text.ToDelta()[0]), Names(text.ToDelta()[1])));
    }

    [TestMethod]
    public void YAttributes_RepeatedKey_LastEntryWins()
    {
        YAttributes attributes = [new("a", 1), new("b", 2), new("a", 3)];

        Assert.AreEqual((2, 3), (attributes.Count, (int)attributes["a"]));
    }

    [TestMethod]
    public void YAttributes_FromSequenceMissingKeyAndEmpty_Behave()
    {
        var attributes = YAttributes.From(new[] { new KeyValuePair<string, YValue>("a", 1) }.Where(_ => true));
        YAttributes none = [];

        Assert.AreEqual((true, false, 0, 1, "a"), (attributes.ContainsKey("a"), attributes.TryGetValue("x", out _), none.Count, attributes.Values.Count(), attributes.Keys.Single()));
        Assert.ThrowsExactly<KeyNotFoundException>(() => _ = attributes["x"]);
        Assert.AreSame(attributes, YAttributes.From(attributes));
        Dictionary<string, YValue>? noDictionary = null;
        YAttributes? converted = noDictionary;
        Assert.IsNull(converted);
        Assert.AreEqual(1, ((System.Collections.IEnumerable)attributes).Cast<object>().Count());
    }

    [TestMethod]
    public void Insert_DetachedText_ReadsItsPendingContent()
    {
        var text = new YText("hello");
        text.Insert(5, " world", [new("bold", true)]);
        text.Remove(0, 1);
        text.InsertEmbed(0, new YMap(), null);
        text.SetAttribute("lang", "en");
        var buffer = new char[text.Length];
        var writer = new System.Buffers.ArrayBufferWriter<char>();
        text.WriteTo(writer);

        Assert.AreEqual(("ello world", 11, 10, "en", true, "ello world", 1), (text.ToString(), text.Length, text.CopyTo(buffer), (string)text.GetAttribute("lang"), text.TryGetAttribute("lang", out _), new string(writer.WrittenSpan), text.GetAttributes().Count));
    }

    [TestMethod]
    public void ToDelta_DetachedTextWithASharedTypeEmbed_ShowsNullAndKeepsTheTypeForTheDocument()
    {
        var map = new YMap();
        var text = new YText("a");
        text.InsertEmbed(1, map);
        text.ApplyDelta([new Delta.Insert(new YMap())]);
        var preview = text.ToDelta();
        var doc = new YDoc();
        doc.GetMap("m")["t"] = text;

        Assert.AreEqual((true, 3), (preview.OfType<Delta.Insert>().Any(i => i.Value.IsNull), text.Length));
        Assert.IsNotNull(map.Doc);
    }

    [TestMethod]
    public void Read_DetachedTextAfterANewOperation_ReflectsIt()
    {
        var text = new YText("ab");
        Assert.AreEqual("ab", text.ToString());

        text.Append("cd");
        text.Format(0, 1, ("bold", true));
        text.ApplyDelta([new Delta.Retain(1), new Delta.Delete(1)]);

        Assert.AreEqual(("acd", 3), (text.ToString(), text.Length));
    }

    [TestMethod]
    public void Read_EmptyDetachedText_IsEmpty()
    {
        var text = new YText();

        Assert.AreEqual((0, "", 0, 0, false, true), (text.Length, text.ToString(), text.ToDelta().Count, text.CopyTo(new char[1]), text.TryGetAttribute("a", out _), text.GetAttribute("a").IsNull));
        text.WriteTo(new System.Buffers.ArrayBufferWriter<char>());
        Assert.AreEqual(0, text.GetAttributes().Count);
    }

    [TestMethod]
    public void Read_DetachedTextWhosePendingOperationThrows_ReadsEmptyAsTheDocumentWould()
    {
        var text = new YText();
        text.Remove(0, 1);
        text.Append("zz");

        Assert.AreEqual("", text.ToString());
    }

    [TestMethod]
    public void Append_AndSubstring_ReadTheText()
    {
        var text = new YDoc().GetText();
        text.Append("hello");
        text.Append(" world", [new("bold", true)]);

        Assert.AreEqual(("lo w", "", "hello world"), (text.Substring(3, 4), text.Substring(11, 0), text.Substring(0, 11)));
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => text.Substring(8, 5));
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => text.Substring(-1, 1));
    }

    [TestMethod]
    public void TryGetAttribute_SetNullAndUnset_TellThemApart()
    {
        var text = new YDoc().GetText();
        text.SetAttribute("n", YValue.Null);

        Assert.AreEqual((true, true, false, true), (text.TryGetAttribute("n", out var n) && n.IsNull, text.GetAttribute("n").IsNull, text.TryGetAttribute("x", out _), text.GetAttribute("x").IsNull));
    }

    [TestMethod]
    public void TryGetAttribute_XmlElementDetachedAndInDocument_ReadsBoth()
    {
        var element = new YXmlElement("p");
        element.SetAttribute("a", "1");
        var doc = new YDoc();
        var fragment = doc.GetXmlFragment("f");
        var inDoc = new YXmlElement("p");
        fragment.Insert(0, inDoc);
        inDoc.SetAttribute("b", "2");

        Assert.AreEqual(("1", false, "2", false, true), ((string)element.GetAttribute("a"), element.TryGetAttribute("b", out _), (string)(inDoc.TryGetAttribute("b", out var b) ? b : default), inDoc.TryGetAttribute("a", out _), inDoc.GetAttribute("a").IsNull));
    }

    [TestMethod]
    public void Event_Target_IsTypedAndDocIsNotNull()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var array = doc.GetArray("a");
        var map = doc.GetMap("m");
        var fragment = doc.GetXmlFragment("x");
        var seen = new List<object>();
        text.Observe(e => seen.Add((e.Target, e.Doc)));
        array.Observe(e => seen.Add((e.Target, e.Doc)));
        map.Observe(e => seen.Add((e.Target, e.Doc)));
        fragment.Observe(e => seen.Add((e.Target, e.Doc)));

        text.Insert(0, "a");
        array.Add(1);
        map["k"] = 1;
        fragment.Insert(0, new YXmlElement("p"));

        Assert.AreEqual(4, seen.Count);
        CollectionAssert.AreEqual(new object[] { (text, doc), (array, doc), (map, doc), (fragment, doc) }, seen);
    }

    [TestMethod]
    public void Keys_OldValue_IsAYValue()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["k"] = "old";
        KeyChange change = default;
        map.Observe(e => change = e.Keys["k"]);

        map["k"] = "new";

        Assert.AreEqual((KeyChangeAction.Update, "old"), (change.Action, (string)change.OldValue));
    }

    private static string Names(Delta op) => string.Join(",", op.Attributes.Keys);
}
