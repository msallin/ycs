using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YXmlShapeTests
{
    private static YXmlFragment Tree()
    {
        var fragment = new YDoc().GetXmlFragment("x");
        var p = new YXmlElement("p");
        fragment.Add(p, new YXmlText("tail"), new YXmlElement("div"));
        p.Add(new YXmlElement("span"), new YXmlElement("b"));
        return fragment;
    }

    [TestMethod]
    public void Add_InsertAndInsertAfter_PlaceTheChildren()
    {
        var fragment = new YDoc().GetXmlFragment("x");
        var a = new YXmlElement("a");
        var c = new YXmlElement("c");

        fragment.Add(a, c);
        fragment.InsertAfter(a, new YXmlElement("b"));
        fragment.InsertAfter(null, new YXmlText("first"));
        fragment.Insert(fragment.Count, new YXmlHook("h"));

        Assert.AreEqual("YXmlText,YXmlElement,YXmlElement,YXmlElement,YXmlHook", string.Join(",", fragment.Select(n => n.GetType().Name)));
        Assert.AreEqual("a,b,c", string.Join(",", fragment.OfType<YXmlElement>().Select(e => e.NodeName)));
    }

    [TestMethod]
    public void Insert_NotAnXmlNode_ThrowsAndInsertsNothing()
    {
        var fragment = new YDoc().GetXmlFragment("x");

        Assert.ThrowsExactly<ArgumentException>(() => fragment.Add(new YXmlElement("a"), new YMap()));
        Assert.ThrowsExactly<ArgumentException>(() => fragment.Insert(0, new YArray()));
        Assert.ThrowsExactly<ArgumentException>(() => fragment.Add(new YText()));
        Assert.AreEqual(0, fragment.Count);
    }

    [TestMethod]
    public void Insert_BeyondTheCount_Throws()
    {
        var fragment = new YDoc().GetXmlFragment("x");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fragment.Insert(1, new YXmlText("t")));
    }

    [TestMethod]
    public void Indexer_SliceAndFirstChild_ReadTheChildren()
    {
        var fragment = Tree();

        Assert.AreEqual(("p", "div", "p,YXmlText", 3), (((YXmlElement)fragment[0]).NodeName, ((YXmlElement)fragment[^1]).NodeName, string.Join(",", fragment[..2].Select(n => n is YXmlElement e ? e.NodeName : n.GetType().Name)), fragment.ToArray().Length));
        Assert.AreSame(fragment[0], fragment.FirstChild);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fragment[3]);
        Assert.IsFalse(fragment.TryGetValue(3, out _));
    }

    [TestMethod]
    public void FirstChild_Empty_IsNull()
    {
        Assert.IsNull(new YDoc().GetXmlFragment("x").FirstChild);
    }

    [TestMethod]
    public void Remove_AtAndRange_RemovesThem()
    {
        var fragment = Tree();

        fragment.RemoveAt(0);
        fragment.RemoveRange(0, 1);

        Assert.AreEqual("div", ((YXmlElement)fragment.Single()).NodeName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => fragment.RemoveRange(0, 2));
    }

    [TestMethod]
    public void CreateTreeWalker_FilterAndOrder_VisitsTheDescendantsInDocumentOrder()
    {
        var fragment = Tree();

        Assert.AreEqual("p,span,b,div", string.Join(",", fragment.CreateTreeWalker(n => n is YXmlElement).Cast<YXmlElement>().Select(e => e.NodeName)));
        Assert.AreEqual(5, fragment.CreateTreeWalker().Count());
    }

    [TestMethod]
    public void QuerySelector_FindsTheFirstOrAllByName()
    {
        var fragment = Tree();

        Assert.AreEqual(("span", null, 1), (fragment.QuerySelector("SPAN")!.NodeName, fragment.QuerySelector("nope"), fragment.QuerySelectorAll("div").Count()));
    }

    [TestMethod]
    public void Attributes_SetGetHasRemove_AreValues()
    {
        var doc = new YDoc();
        var element = new YXmlElement("p");
        doc.GetXmlFragment("x").Add(element);

        element.SetAttribute("n", 1.5);
        element.SetAttribute("flag", true);
        element.SetAttribute("none", YValue.Null);
        element.RemoveAttribute("flag");

        Assert.AreEqual((1.5, true, true, false, false), (element.GetAttribute("n").GetDouble(), element.GetAttribute("none").IsNull, element.HasAttribute("none"), element.HasAttribute("flag"), element.HasAttribute("zz")));
        Assert.AreEqual("<p n=\"1.5\" none=\"null\"></p>", element.ToString());
    }

    [TestMethod]
    public void Attributes_AtASnapshot_AreWhatWasThere()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var element = new YXmlElement("p");
        doc.GetXmlFragment("x").Add(element);
        element.SetAttribute("a", "1");
        var snapshot = doc.CreateSnapshot();
        element.SetAttribute("a", "2");
        element.SetAttribute("b", "3");

        Assert.AreEqual(("1", 1, 2), (element.GetAttributes(snapshot)["a"].GetString(), element.GetAttributes(snapshot).Count, element.GetAttributes().Count));
    }

    [TestMethod]
    public void ChildrenAtASnapshot_AreWhatWasThere()
    {
        var doc = new YDoc();
        var fragment = doc.GetXmlFragment("x");
        fragment.Add(new YXmlElement("a"));
        var snapshot = doc.CreateSnapshot();
        fragment.Add(new YXmlElement("b"));

        Assert.AreEqual((1, 2), (fragment.ToArray(snapshot).Length, fragment.Count));
    }

    [TestMethod]
    public void NotInADocument_AttributesAndChildren_AreReadable()
    {
        var element = new YXmlElement("p");
        element.SetAttribute("a", 1);
        var child = new YXmlElement("c");
        element.Add(child);
        element.InsertAfter(child, new YXmlElement("d"));

        Assert.AreEqual((1, 1, 2, "<p a=\"1\"><c></c><d></d></p>"), (element.GetAttributes().Count, element.GetAttribute("a").GetInt32(), element.Count, element.ToString()));
    }

    [TestMethod]
    public void Clone_CopiesAttributesAndChildren()
    {
        var fragment = Tree();
        var p = (YXmlElement)fragment[0];
        p.SetAttribute("id", "x");

        var clone = p.Clone();

        Assert.AreEqual(p.ToString(), clone.ToString());
        Assert.AreNotSame(p[0], clone[0]);
    }

    [TestMethod]
    public void SiblingsAndHook_AreTypes()
    {
        var fragment = Tree();
        var p = (YXmlElement)fragment[0];

        Assert.AreSame(fragment[1], p.NextSibling);
        Assert.IsNull(p.PrevSibling);
        var hook = new YXmlHook("h");
        hook["k"] = 1;
        fragment.Add(hook);
        Assert.AreEqual(1, ((YXmlHook)fragment[3])["k"].GetInt32());
    }
}
