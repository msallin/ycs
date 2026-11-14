// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YXmlTests
{
    /// <summary>
    /// Byte-level compatibility with real Yjs. Produced by 'interop/js/check-xml.mjs' running
    /// against yjs13 -- a y-prosemirror-shaped document:
    /// '&lt;paragraph align="center" class="intro"&gt;&lt;bold&gt;Hello &lt;/bold&gt;world&lt;em title="shout"&gt;!!!&lt;/em&gt;&lt;/paragraph&gt;'
    /// (a sibling 'scratch' element was inserted and deleted in the same document, so the update
    /// also carries a non-empty delete set). Re-generate with 'interop/run.sh' if the wire format
    /// or this fixture's shape changes.
    /// </summary>
    private const string Yjs13XmlUpdateV1 = "AQ3TmNW2BAAHAQN4bWwDCXBhcmFncmFwaAcA05jVtgQABgYA05jVtgQBBGJvbGQEdHJ1ZYTTmNW2BAIGSGVsbG8ghtOY1bYECARib2xkBG51bGyE05jVtgQJBXdvcmxkh9OY1bYEAQMCZW0HANOY1bYEDwYEANOY1bYEEAMhISEoANOY1bYEDwV0aXRsZQF3BXNob3V0KADTmNW2BAAFYWxpZ24BdwZjZW50ZXIoANOY1bYEAAVjbGFzcwF3BWludHJvgdOY1bYEAAEB05jVtgQBFwE=";
    private const string Yjs13XmlUpdateV2 = "AAMAAwEG07Gq7QgLDAADAAwCUBwCQl4BAAATBwEGAIQAhgCEAIcABwAEACgCgT4zeG1scGFyYWdyYXBoYm9sZEhlbGxvIGJvbGR3b3JsZGVtISEhdGl0bGVhbGlnbmNsYXNzAwkEBgQFAgNFAQMBAAAEAwYDBgJBAgENAHh+dwVzaG91dHcGY2VudGVydwVpbnRybwHTmNW2BAEXAA==";

    [TestMethod]
    [DataRow(Yjs13XmlUpdateV1, false, DisplayName = "v1 update")]
    [DataRow(Yjs13XmlUpdateV2, true, DisplayName = "v2 update")]
    public void ReadsXmlTreeProducedByYjs(string base64Update, bool v2)
    {
        var doc = Apply(base64Update, v2);
        var frag = doc.GetXmlFragment("xml");

        // The 'scratch' sibling was inserted then deleted; only 'paragraph' should survive.
        Assert.AreEqual(1, frag.Length);

        var paragraph = frag.Get(0) as YXmlElement;
        Assert.IsNotNull(paragraph);
        Assert.AreEqual("paragraph", paragraph.NodeName);
        Assert.AreSame(frag, paragraph.Parent);

        var attrs = paragraph.GetAttributes();
        Assert.AreEqual(2, attrs.Count);
        Assert.AreEqual("center", attrs["align"]);
        Assert.AreEqual("intro", attrs["class"]);
        Assert.AreEqual("center", paragraph.GetAttribute("align"));
        Assert.IsTrue(paragraph.HasAttribute("class"));
        Assert.IsFalse(paragraph.HasAttribute("missing"));

        var children = paragraph.ToArray();
        Assert.AreEqual(2, children.Count);

        var boldText = children[0] as YXmlText;
        Assert.IsNotNull(boldText);
        Assert.AreSame(boldText, paragraph.FirstChild);
        Assert.AreEqual("Hello world", boldText.ToString().Replace("<bold>", "").Replace("</bold>", ""));

        var delta = boldText.ToDelta();
        Assert.IsTrue(delta.Any(d => (string)d.Insert == "Hello " && Equals(d.Attributes?["bold"], true)));

        var em = children[1] as YXmlElement;
        Assert.IsNotNull(em);
        Assert.AreEqual("em", em.NodeName);
        Assert.AreEqual("shout", em.GetAttribute("title"));
        Assert.AreSame(boldText, em.PrevSibling);
        Assert.AreSame(em, boldText.NextSibling);
        Assert.IsNull(em.NextSibling);

        var emText = em.FirstChild as YXmlText;
        Assert.IsNotNull(emText);
        Assert.AreEqual("!!!", emText.ToString());

        Assert.AreEqual(
            "<paragraph align=\"center\" class=\"intro\"><bold>Hello </bold>world<em title=\"shout\">!!!</em></paragraph>",
            frag.ToString());
        Assert.AreEqual(frag.ToString(), frag.ToJson());
    }

    [TestMethod]
    [DataRow(Yjs13XmlUpdateV1, false, DisplayName = "v1 re-encode")]
    [DataRow(Yjs13XmlUpdateV2, true, DisplayName = "v2 re-encode")]
    public void ReEncodesXmlToAnUpdateYjsCanRead(string base64Update, bool v2)
    {
        var doc = Apply(base64Update, v2);
        var reEncoded = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1();

        var copy = new YDoc();
        if (v2)
        {
            copy.ApplyUpdateV2(reEncoded);
        }
        else
        {
            copy.ApplyUpdateV1(reEncoded);
        }

        Assert.AreEqual(doc.GetXmlFragment("xml").ToString(), copy.GetXmlFragment("xml").ToString());

        if (!v2)
        {
            CollectionAssert.AreEqual(Convert.FromBase64String(base64Update), reEncoded,
                "v1 re-encoding drifted from the bytes Yjs produced");
        }
    }

    [TestMethod]
    public void FragmentInsertDeleteAndToString()
    {
        var doc = new YDoc();
        var frag = doc.GetXmlFragment("xml");

        var p = new YXmlElement("p");
        p.SetAttribute("id", "1");
        var text = new YXmlText("hi");
        p.Insert(0, new object[] { text });
        frag.Insert(0, new object[] { p });

        Assert.AreEqual(1, frag.Length);
        Assert.AreEqual("<p id=\"1\">hi</p>", frag.ToString());

        var second = new YXmlElement("q");
        frag.Push(new object[] { second });
        Assert.AreEqual(2, frag.Length);
        Assert.AreSame(second, frag.Get(1));

        var first = new YXmlElement("first");
        frag.Unshift(new object[] { first });
        Assert.AreEqual(3, frag.Length);
        Assert.AreSame(first, frag.Get(0));

        frag.Delete(1, 1); // removes 'p'
        Assert.AreEqual(2, frag.Length);
        CollectionAssert.DoesNotContain(frag.ToArray().ToList(), p);

        var inserted = new YXmlElement("inserted");
        frag.InsertAfter(first, new object[] { inserted });
        Assert.AreEqual(3, frag.Length);
        Assert.AreSame(inserted, frag.Get(1));
    }

    [TestMethod]
    public void ElementAttributesAndPrelimBeforeIntegration()
    {
        // Attributes and children set before the element is attached to a doc go through the
        // 'prelim' buffers and must survive integration.
        var el = new YXmlElement("div");
        el.SetAttribute("class", "prelim");
        el.SetAttribute("temp", "x");
        el.RemoveAttribute("temp");
        var child = new YXmlText("child");
        el.Insert(0, new object[] { child });

        var doc = new YDoc();
        doc.GetXmlFragment("xml").Insert(0, new object[] { el });

        Assert.AreEqual("prelim", el.GetAttribute("class"));
        Assert.IsFalse(el.HasAttribute("temp"));

        Assert.AreEqual(1, el.Length);
        Assert.AreEqual(1, el.ToArray().Count);
        Assert.AreEqual("child", el.Get(0).ToString());
        Assert.AreEqual("<div class=\"prelim\">child</div>", el.ToString());
    }

    [TestMethod]
    public void CloneCopiesAttributesAndChildrenNotIdentity()
    {
        var doc = new YDoc();
        var frag = doc.GetXmlFragment("xml");
        var el = new YXmlElement("p");
        el.SetAttribute("a", "1");
        var text = new YXmlText("hi");
        el.Insert(0, new object[] { text });
        frag.Insert(0, new object[] { el });

        // Per Yjs' own contract ("the content is only readable after it has been included
        // somewhere in the Ydoc"), attach the clone before reading it back.
        var clone = el.Clone();
        frag.Push(new object[] { clone });

        Assert.AreEqual("1", clone.GetAttribute("a"));
        Assert.AreEqual(el.ToString(), clone.ToString());
        Assert.AreNotSame(el, clone);

        // Mutating the clone must not affect the original.
        clone.SetAttribute("a", "2");
        Assert.AreEqual("1", el.GetAttribute("a"));
        Assert.AreEqual("2", clone.GetAttribute("a"));
    }

    [TestMethod]
    public void QuerySelectorAndTreeWalkerTraverseDepthFirst()
    {
        var doc = new YDoc();
        var frag = doc.GetXmlFragment("xml");

        var root = new YXmlElement("root");
        var a = new YXmlElement("a");
        var b = new YXmlElement("b");
        a.Insert(0, new object[] { new YXmlText("in-a") });
        root.Insert(0, new object[] { a, b });
        frag.Insert(0, new object[] { root });

        Assert.AreSame(a, frag.QuerySelector("A"));
        Assert.AreSame(b, frag.QuerySelector("b"));
        Assert.IsNull(frag.QuerySelector("missing"));

        var all = frag.QuerySelectorAll("*missing*").ToList();
        Assert.AreEqual(0, all.Count);

        var elements = frag.CreateTreeWalker(t => t is YXmlElement).ToList();
        CollectionAssert.AreEqual(new object[] { root, a, b }, elements);

        var everything = frag.CreateTreeWalker().ToList();
        Assert.AreEqual(4, everything.Count); // root, a, in-a text, b
    }

    [TestMethod]
    public void HookRoundTripsThroughTheWireFormatLikeAMap()
    {
        var doc1 = new YDoc();
        var hook = new YXmlHook("custom-widget");
        hook.Set("editable", false);
        doc1.GetXmlFragment("xml").Insert(0, new object[] { hook });

        var update = doc1.EncodeStateAsUpdateV1();

        var doc2 = new YDoc();
        doc2.ApplyUpdateV1(update);

        var readBack = doc2.GetXmlFragment("xml").Get(0) as YXmlHook;
        Assert.IsNotNull(readBack);
        Assert.AreEqual("custom-widget", readBack.HookName);
        Assert.AreEqual(false, readBack.Get("editable"));

        // Also round-trips through v2.
        var doc3 = new YDoc();
        doc3.ApplyUpdateV2(doc1.EncodeStateAsUpdateV2());
        var readBack2 = doc3.GetXmlFragment("xml").Get(0) as YXmlHook;
        Assert.AreEqual("custom-widget", readBack2.HookName);
    }

    [TestMethod]
    public void GetXmlElementAndGetXmlTextTopLevelAccessors()
    {
        var doc = new YDoc();
        doc.GetXmlElement("el").SetAttribute("a", "b");
        doc.GetXmlText("txt").Insert(0, "hello", null);

        var update = doc.EncodeStateAsUpdateV1();
        var copy = new YDoc();
        copy.ApplyUpdateV1(update);

        Assert.AreEqual("b", copy.GetXmlElement("el").GetAttribute("a"));
        Assert.AreEqual("hello", copy.GetXmlText("txt").ToString());
    }

    [TestMethod]
    public void XmlEventReportsChildListAndAttributeChanges()
    {
        var doc = new YDoc();
        var el = doc.GetXmlElement("el");

        YXmlEvent captured = null;
        el.EventHandler += (s, e) => captured = e.Event as YXmlEvent;

        el.SetAttribute("a", "1");
        Assert.IsNotNull(captured);
        Assert.IsFalse(captured.ChildListChanged);
        CollectionAssert.Contains(captured.AttributesChanged.ToList(), "a");

        captured = null;
        el.Insert(0, new object[] { new YXmlText("hi") });
        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.ChildListChanged);
    }

    private static YDoc Apply(string base64Update, bool v2)
    {
        var doc = new YDoc();
        var update = Convert.FromBase64String(base64Update);
        if (v2)
        {
            doc.ApplyUpdateV2(update);
        }
        else
        {
            doc.ApplyUpdateV1(update);
        }

        return doc;
    }
}
