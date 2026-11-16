using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Changes to a text before it is in a document are replayed, in order, when it is added.</summary>
[TestClass]
public class YTextPendingTests
{
    [TestMethod]
    public void Integrate_PendingChanges_GiveTheSameTextAsDirectChanges()
    {
        var direct = new YDoc(new YDocOptions { ClientId = 1 }).GetText("text");
        Edit(direct);
        var prelim = new YText("ab");
        Edit(prelim, skipFirstInsert: true);

        new YDoc(new YDocOptions { ClientId = 1 }).GetMap("map").Set("text", prelim);

        Assert.AreEqual(direct.ToString(), prelim.ToString());
        Assert.AreEqual(Describe(direct.ToDelta()), Describe(prelim.ToDelta()));
        Assert.AreEqual("en", prelim.GetAttribute("lang"));
        Assert.IsNull(prelim.GetAttribute("gone"));
    }

    [TestMethod]
    public void Integrate_PendingApplyDeltaWithoutSanitize_KeepsTheTrailingNewlineAsYjs()
    {
        // Yjs' pending applyDelta is 'this._pending.push(() => this.applyDelta(delta))': the option is not kept, so the
        // delta is applied with sanitize (the default), which keeps the newline that sanitize: false would drop.
        var prelim = new YText();
        prelim.ApplyDelta(new List<Delta> { new Delta.Insert("x\n") }, sanitize: false);

        new YDoc(new YDocOptions { ClientId = 1 }).GetMap("map").Set("text", prelim);

        Assert.AreEqual("x\n", prelim.ToString());
    }

    private static void Edit(YText text, bool skipFirstInsert = false)
    {
        if (!skipFirstInsert)
        {
            text.Insert(0, "ab");
        }

        text.Insert(1, "xyz", new Dictionary<string, YValue> { ["bold"] = true });
        text.InsertEmbed(0, YValue.FromObject(new Dictionary<string, object> { ["image"] = "i.png" }));
        text.Format(2, 2, new Dictionary<string, YValue> { ["italic"] = true });
        text.Remove(3, 1);
        text.ApplyDelta(new List<Delta> { new Delta.Retain(1), new Delta.Insert("q") });
        text.SetAttribute("lang", "en");
        text.SetAttribute("gone", 1);
        text.RemoveAttribute("gone");
    }

    private static string Describe(IReadOnlyList<Delta> delta)
    {
        var parts = new List<string>();
        foreach (var op in delta)
        {
            var attributes = new List<string>();
            foreach (var kvp in op.Attributes ?? new Dictionary<string, YValue>())
            {
                attributes.Add($"{kvp.Key}={kvp.Value}");
            }

            parts.Add($"{(op.Inserted().TryGetString(out var s) ? s : "<embed>")}[{string.Join(",", attributes)}]");
        }

        return string.Join(" ", parts);
    }
}
