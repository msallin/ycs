// Reads the base64 v1 update produced by 'interop/js/check-xml.mjs' (a y-prosemirror-shaped
// Y.XmlFragment: XmlFragment > XmlElement (with attributes) > XmlText (with formatting) plus a
// nested XmlElement and a prior deletion), decodes it with Ycs, asserts the resulting tree, edits
// it the way a .NET server consuming the document would, and prints Ycs' own v1 + v2 updates plus
// a summary of what it read -- for the JS side to apply back into real Yjs and verify.
// Usage: dotnet run -- <base64 v1 update>
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Ycs;

void Fail(string message)
{
    Console.Error.WriteLine("FAIL: " + message);
    Environment.Exit(1);
}

void Check(bool condition, string message)
{
    if (!condition)
    {
        Fail(message);
    }
}

var doc = new YDoc();
doc.ApplyUpdateV1(Convert.FromBase64String(args[0]));

var frag = doc.GetXmlFragment("xml");

// The JS side deleted the 'scratch' element before encoding, so only 'paragraph' should remain.
Check(frag.Length == 1, $"expected 1 top-level child (the deleted 'scratch' sibling should be gone), got {frag.Length}");

var paragraph = frag.Get(0) as YXmlElement;
Check(paragraph != null, "top-level child is not a YXmlElement");
Check(paragraph.NodeName == "paragraph", $"expected <paragraph>, got <{paragraph?.NodeName}>");
Check(paragraph.Parent == frag, "paragraph.Parent does not point back to the fragment");

var attrs = paragraph.GetAttributes();
Check(attrs.Count == 2 && Equals(attrs["align"], "center") && Equals(attrs["class"], "intro"),
    $"paragraph attributes mismatch: {JsonConvert.SerializeObject(attrs)}");

var children = paragraph.ToArray();
Check(children.Count == 2, $"expected 2 children under <paragraph>, got {children.Count}");

var boldText = children[0] as YXmlText;
Check(boldText != null, "first paragraph child is not a YXmlText");
Check(ReferenceEquals(paragraph.FirstChild, boldText), "paragraph.FirstChild does not match the first child");
var delta = boldText.ToDelta();
Check(delta.Any(d => d.Insert is string s && s.Contains("Hello") && d.Attributes != null &&
                      d.Attributes.TryGetValue("bold", out var boldVal) && Equals(boldVal, true)),
    "bold formatting lost on read");

var em = children[1] as YXmlElement;
Check(em != null && em.NodeName == "em", "second paragraph child is not <em>");
Check(Equals(em.GetAttribute("title"), "shout"), "em/@title lost on read");
Check(ReferenceEquals(em.PrevSibling, boldText), "em.PrevSibling should be the bold text node");
Check(ReferenceEquals(boldText.NextSibling, em), "boldText.NextSibling should be <em>");

var emText = em.FirstChild as YXmlText;
Check(emText != null && emText.ToString() == "!!!", "em text content lost on read");

// The tree-walker / query surface.
var paragraphs = frag.QuerySelectorAll("paragraph").ToList();
Check(paragraphs.Count == 1 && ReferenceEquals(paragraphs[0], paragraph), "querySelectorAll('paragraph') should find the one <paragraph>");
Check(ReferenceEquals(frag.QuerySelector("EM"), em), "querySelector is not case-insensitive on tag name");

var walked = frag.CreateTreeWalker(t => t is YXmlElement || t is YXmlText).ToList();
Check(walked.Count == 4, $"expected 4 nodes walked (paragraph, boldText, em, emText), got {walked.Count}");
Check(ReferenceEquals(walked[0], paragraph) && ReferenceEquals(walked[1], boldText) &&
      ReferenceEquals(walked[2], em) && ReferenceEquals(walked[3], emText),
    "tree walker did not visit nodes in the expected (depth-first, document) order");

var toStringBeforeEdit = frag.ToString();

// Ycs makes its own edits, the way a .NET server consuming this document would.
doc.Transact(_ =>
{
    paragraph.SetAttribute("edited-by", "ycs");
    em.SetAttribute("shout-level", "11");

    var tail = new YXmlText();
    tail.Insert(0, "tail", new Dictionary<string, object> { { "italic", true } });
    frag.Push(new object[] { tail });
});

var toStringAfterEdit = frag.ToString();

Console.WriteLine(JsonConvert.SerializeObject(new
{
    readTree = new
    {
        paragraphNodeName = paragraph.NodeName,
        attributes = attrs,
        emNodeName = em.NodeName,
        emTitle = em.GetAttribute("title"),
        emText = emText.ToString()
    },
    toStringBeforeEdit,
    toStringAfterEdit,
    updateV1 = Convert.ToBase64String(doc.EncodeStateAsUpdateV1()),
    updateV2 = Convert.ToBase64String(doc.EncodeStateAsUpdateV2())
}, Formatting.Indented));
