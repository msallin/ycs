using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The cleanup of formatting after a remote change runs once per transaction, after its
/// AfterTransaction handlers, and only for a text that has held formatting (Yjs 13.6's
/// <c>_hasFormatting</c>). The transaction counts and the encodings are those of Yjs 13.6.32 running
/// the same steps with the same client ids.
/// </summary>
[TestClass]
public class FormattingCleanupTests
{
    // What Yjs 13.6.32 leaves after the same steps with the same client ids: no format deleted,
    // [ab bold][cd bold][ef].
    private const string ConcurrentBoldAfterSync =
        "AgICAMYBAQECBGJvbGQEdHJ1ZYYBBQRib2xkBG51bGwFAQAEAQR0ZXh0AmFihAEBAmNkhAEDAmVmRgEABGJvbGQEdHJ1ZcYBAwEEBGJvbGQEbnVsbAA=";

    [TestMethod]
    public void ApplyUpdate_WithFormatting_RunsCleanupTransaction()
    {
        var (a, b) = TwoDocsWith("abcdef");
        a.GetText("text").Format(0, 4, Bold());
        b.GetText("text").Format(2, 4, Bold());
        var transactions = 0;
        b.AfterTransaction += (_, _) => transactions++;

        b.ApplyUpdate(a.EncodeStateAsUpdate(b.EncodeStateVector()));
        a.ApplyUpdate(b.EncodeStateAsUpdate(a.EncodeStateVector()));

        Assert.AreEqual(2, transactions, "the update and the formatting cleanup, as in Yjs");
        Assert.AreEqual(ConcurrentBoldAfterSync, Convert.ToBase64String(b.EncodeStateAsUpdate()));
        Assert.AreEqual("[ab bold][cd bold][ef]", Describe(b.GetText("text")));
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

        c.ApplyUpdate(a.EncodeStateAsUpdate());

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

        e.ApplyUpdate(d.EncodeStateAsUpdate());
        d.GetText("text").Delete(0, 1);
        e.ApplyUpdate(d.EncodeStateAsUpdate(e.EncodeStateVector()));

        Assert.AreEqual(2, transactions, "one per update, as in Yjs");
        Assert.AreEqual("y", text.ToString());
    }

    private static (YDoc A, YDoc B) TwoDocsWith(string content)
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        a.GetText("text").Insert(0, content);
        b.GetText("text");
        b.ApplyUpdate(a.EncodeStateAsUpdate());
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

    [TestMethod]
    public void ApplyUpdate_FormatsInManyTexts_CleansUpInLinearTime()
    {
        // Every formatted text is cleaned up once; finding it among those already found took O(n).
        var source = new YDoc(new YDocOptions { ClientId = 2 });
        var target = new YDoc(new YDocOptions { ClientId = 3 });
        var map = source.GetMap("m");
        var texts = new List<YText>();
        source.Transact(_ =>
        {
            for (var i = 0; i < 40_000; i++)
            {
                var text = new YText();
                map.Set("t" + i, text);
                text.Insert(0, "ab", Bold());
                texts.Add(text);
            }
        });
        target.ApplyUpdate(source.EncodeStateAsUpdate(), null, limits: UpdateLimits.Unlimited);
        byte[] update = null;
        source.Updated += (_, e) => update = e.Update.ToArray();
        source.Transact(_ => texts.ForEach(text => text.Format(0, 2, new Dictionary<string, object> { ["italic"] = true })));

        var stopwatch = Stopwatch.StartNew();
        target.ApplyUpdate(update, null, limits: UpdateLimits.Unlimited);

        Assert.IsLessThan(1_500, stopwatch.ElapsedMilliseconds);
    }

    private static Dictionary<string, object> Bold() => new() { ["bold"] = true };
}
