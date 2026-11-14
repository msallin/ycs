using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The cleanup of formatting after a remote change runs only for a text that has held formatting
/// (Yjs 13.6's <c>_hasFormatting</c>). The transaction counts are those of Yjs 13.6.32 running the
/// same steps with the same client ids.
/// </summary>
[TestClass]
public class FormattingCleanupTests
{
    // What the cleanup leaves: the two documents agree, and the cleanup deleted three formats (the
    // delete set at the end), so all of "abcdef" is bold. Yjs 13.6.32 deletes none and ends with
    // [ab bold][cd bold][ef], encoded as
    // "AgICAMYBAQECBGJvbGQEdHJ1ZYYBBQRib2xkBG51bGwFAQAEAQR0ZXh0AmFihAEBAmNkhAEDAmVmRgEABGJvbGQEdHJ1ZcYBAwEEBGJvbGQEbnVsbAA=":
    // the formatting algorithms here (cleanupFormattingGap, formatText) are those of an earlier Yjs.
    // This pins today's result, so that a change to it is deliberate.
    private const string ConcurrentBoldAfterSync =
        "AgICAMEBAQECAYYBBQRib2xkBG51bGwFAQAEAQR0ZXh0AmFihAEBAmNkhAEDAmVmRgEABGJvbGQEdHJ1ZcEBAwEEAQICAQABAQEHAQ==";

    [TestMethod]
    public void ApplyUpdate_WithFormatting_RunsCleanupTransaction()
    {
        var (a, b) = TwoDocsWith("abcdef");
        a.GetText("text").Format(0, 4, Bold());
        b.GetText("text").Format(2, 4, Bold());
        var transactions = 0;
        b.AfterTransaction += (_, _) => transactions++;

        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1(b.EncodeStateVector()));
        a.ApplyUpdateV1(b.EncodeStateAsUpdateV1(a.EncodeStateVector()));

        Assert.AreEqual(2, transactions, "the update and the formatting cleanup, as in Yjs");
        Assert.AreEqual(ConcurrentBoldAfterSync, Convert.ToBase64String(b.EncodeStateAsUpdateV1()));
        Assert.AreEqual("[abcdef bold]", Describe(b.GetText("text")));
    }

    [TestMethod]
    public void ApplyUpdate_FirstFormattingArrives_RunsCleanupTransaction()
    {
        var (a, _) = TwoDocsWith("abcdef");
        a.GetText("text").Format(0, 2, Bold());
        var c = new YDoc(new YDocOptions { ClientId = 3 });
        c.GetText("text");
        var transactions = 0;
        c.AfterTransaction += (_, _) => transactions++;

        c.ApplyUpdateV1(a.EncodeStateAsUpdateV1());

        Assert.AreEqual(2, transactions);
    }

    [TestMethod]
    public void ApplyUpdate_WithoutFormatting_RunsNoSecondTransaction()
    {
        var d = new YDoc(new YDocOptions { ClientId = 4 });
        d.GetText("text").Insert(0, "xy");
        var e = new YDoc(new YDocOptions { ClientId = 5 });
        var text = e.GetText("text");
        var transactions = 0;
        e.AfterTransaction += (_, _) => transactions++;

        e.ApplyUpdateV1(d.EncodeStateAsUpdateV1());
        d.GetText("text").Delete(0, 1);
        e.ApplyUpdateV1(d.EncodeStateAsUpdateV1(e.EncodeStateVector()));

        Assert.AreEqual(2, transactions, "one per update, as in Yjs");
        Assert.AreEqual("y", text.ToString());
    }

    private static (YDoc A, YDoc B) TwoDocsWith(string content)
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        a.GetText("text").Insert(0, content);
        b.GetText("text");
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        return (a, b);
    }

    private static string Describe(YText text)
    {
        var parts = new System.Text.StringBuilder();
        foreach (var op in text.ToDelta())
        {
            parts.Append('[').Append(op.Insert);
            foreach (var attribute in op.Attributes ?? new Dictionary<string, object>())
            {
                parts.Append(' ').Append(attribute.Key);
            }

            parts.Append(']');
        }

        return parts.ToString();
    }

    private static Dictionary<string, object> Bold() => new() { ["bold"] = true };
}
