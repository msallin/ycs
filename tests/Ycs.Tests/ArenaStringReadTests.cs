using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The text of a String item is read from the character arena. A span of it must not outlive user code or the end of a batch,
/// which may compact the arena: <see cref="YText.WriteTo"/> asks the writer for its span first. They run with the default page
/// sizes and without forced compaction whatever the run mode.
/// </summary>
[TestClass]
public class ArenaStringReadTests
{
    private static (int First, int Max, bool Copy, TestHooks.CompactionMode Compaction) _mode;

    [ClassInitialize]
    public static void SaveMode(TestContext context) => _mode = (TestHooks.ArenaPages<char>.First, TestHooks.ArenaPages<char>.Max, TestHooks.CopyOnMerge, TestHooks.Compaction);

    [TestInitialize]
    public void NoMode()
    {
        TestHooks.ArenaPages<char>.First = 0;
        TestHooks.ArenaPages<char>.Max = 0;
        TestHooks.CopyOnMerge = false;
        TestHooks.Compaction = TestHooks.CompactionMode.None;
    }

    [TestCleanup]
    public void RestoreMode() => (TestHooks.ArenaPages<char>.First, TestHooks.ArenaPages<char>.Max, TestHooks.CopyOnMerge, TestHooks.Compaction) = _mode;

    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    // Three items of the text "t": "ccc", "bbb", "aaa".
    private static YText ThreeItems(YDoc doc)
    {
        var text = doc.GetText("t");
        text.Insert(0, "aaa");
        text.Insert(0, "bbb");
        text.Insert(0, "ccc");
        return text;
    }

    // The offset of "aaa"'s range, which a compaction moves.
    private static long FirstPayload(YDoc doc, YText text) => doc.Store.Slab.Read(doc.Store.Find(new ItemId(1, 0)).Handle).Payload;

    // Many runs on many pages of the arena, then nearly all of them deleted: the arena holds more garbage than text.
    private static void MakeGarbage(YText other)
    {
        for (var i = 0; i < 400; i++)
        {
            other.Insert(other.Length, new string('x', 100) + "|");
        }

        for (var i = 0; i < 400; i++)
        {
            other.Remove(i + 1, 99);
        }
    }

    [TestMethod]
    public void WriteTo_WriterCompactsTheArenaInGetSpan_WritesTheText()
    {
        using var first256 = StaticScope.Set(ref TestHooks.ArenaPages<char>.First, 256);
        var doc = NewDoc();
        var text = ThreeItems(doc);
        var other = doc.GetText("u");
        other.Insert(0, new string('x', 200));
        other.Remove(0, 200);
        var before = FirstPayload(doc, text);
        var writer = new EditingWriter(spans =>
        {
            other.Insert(0, "y");
            doc.Store.Slab.CompactChars();
        });

        text.WriteTo(writer);

        Assert.AreEqual(("cccbbbaaa", true), (writer.Text, FirstPayload(doc, text) != before));
    }

    [TestMethod]
    public void WriteTo_WriterEditsAnotherTextEnoughToCompact_WritesTheText()
    {
        using var first256 = StaticScope.Set(ref TestHooks.ArenaPages<char>.First, 256);
        var doc = NewDoc();
        var text = ThreeItems(doc);
        var other = doc.GetText("u");
        other.Insert(0, new string('x', 200));
        other.Remove(0, 200);
        var before = FirstPayload(doc, text);
        var writer = new EditingWriter(spans =>
        {
            if (spans == 1)
            {
                MakeGarbage(other);
            }
        });

        text.WriteTo(writer);

        Assert.AreEqual(("cccbbbaaa", true), (writer.Text, FirstPayload(doc, text) != before));
    }

    [TestMethod]
    public void WriteTo_WriterSplitsTheItemItIsWriting_WritesTheTextAsItIs()
    {
        var doc = NewDoc();
        var text = ThreeItems(doc);
        var writer = new EditingWriter(spans =>
        {
            if (spans == 2)
            {
                text.Insert(4, "-");
            }
        });

        text.WriteTo(writer);

        Assert.AreEqual((text.ToString(), "cccb-bbaaa"), (writer.Text, "cccb-bbaaa"));
    }

    [TestMethod]
    public void WriteTo_WriterExtendsTheItemItIsWriting_WritesTheRestInAnotherPiece()
    {
        var doc = NewDoc();
        var text = ThreeItems(doc);
        var writer = new EditingWriter(spans =>
        {
            if (spans == 3)
            {
                text.Insert(9, "dd");
            }
        });

        text.WriteTo(writer);

        Assert.AreEqual((text.ToString(), "cccbbbaaadd"), (writer.Text, "cccbbbaaadd"));
    }

    [TestMethod]
    public void WriteTo_WriterCollectsTheTextItIsWriting_WritesNothingMore()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var inner = new YText();
        map["t"] = inner;
        inner.Insert(0, "abc");
        var writer = new EditingWriter(spans => map.Remove("t"));

        inner.WriteTo(writer);

        Assert.AreEqual("", writer.Text);
    }

    [TestMethod]
    public void Elements_SurrogatePair_GivesOneStringPerCodeUnit()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a😀");
        var item = doc.Store.Find(new ItemId(1, 0));
        var elements = new List<object>();

        item.AppendContentTo(elements);

        Assert.AreEqual("a,\uD83D,\uDE00", string.Join(",", elements));
    }

    [TestMethod]
    public void Chars_StringItemInATinyArena_ReadsTheArena()
    {
        TestHooks.ArenaPages<char>.First = 3;
        TestHooks.ArenaPages<char>.Max = 3;
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "hello world");
        var item = doc.Store.Find(new ItemId(1, 0));

        Assert.AreEqual("hello world", item.Chars.ToString());
    }

    private sealed class EditingWriter(Action<int> onSpan) : IBufferWriter<char>
    {
        private readonly StringBuilder _written = new();
        private char[] _span = [];
        private int _spans;

        public string Text => _written.ToString();

        public void Advance(int count) => _written.Append(_span, 0, count);

        public Memory<char> GetMemory(int sizeHint = 0) => _span = new char[Math.Max(sizeHint, 1)];

        public Span<char> GetSpan(int sizeHint = 0)
        {
            onSpan(++_spans);
            return GetMemory(sizeHint).Span;
        }
    }
}
