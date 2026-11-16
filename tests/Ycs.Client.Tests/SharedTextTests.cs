namespace Ycs.Client.Tests;

[TestClass]
public class SharedTextTests
{
    [TestMethod]
    public void Edit_Typing_IsOneUpdateThatAnotherApplies()
    {
        var author = new Page();
        var reader = new Page();

        author.Text.Edit("hi");
        reader.Receive(author.Sent.Single());

        Assert.AreEqual("hi", reader.Text.Text);
    }

    [TestMethod]
    public void Edit_Replacement_ChangesTheTextAndTheYText()
    {
        var author = new Page();
        var reader = new Page();
        author.Text.Edit("hello world");

        author.Text.Edit(new TextDiff(6, 5, "there"));
        foreach (var update in author.Sent)
        {
            reader.Receive(update);
        }

        Assert.AreEqual(("hello there", "hello there"), (author.Text.Text, reader.Text.Text));
    }

    [TestMethod]
    public void Edit_BeyondTheText_ThrowsAndChangesNothing()
    {
        var page = new Page();
        page.Text.Edit("ab");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => page.Text.Edit(new TextDiff(1, 2, "x")));
        Assert.AreEqual(("ab", 1), (page.Text.Text, page.Sent.Count));
    }

    [TestMethod]
    public void Show_RemoteUpdate_IsNoEditOfItsOwn()
    {
        var author = new Page();
        var reader = new Page();
        author.Text.Edit("hi");

        reader.Receive(author.Sent[0]);

        Assert.AreEqual(0, reader.Sent.Count);
    }

    // Another client wrote "\r\n" and an embed: the textarea would drop the "\r" and count the embed, so both go, in a
    // transaction of this client's that the others receive.
    [TestMethod]
    public void Show_CarriageReturnAndEmbedOfAnother_AreRemovedForEveryone()
    {
        var other = new YDoc();
        var updates = new List<byte[]>();
        other.Updated += (_, e) => updates.Add(e.Update.ToArray());
        var text = other.GetText(SharedText.Name);
        text.Insert(0, "a\r\nb");
        text.InsertEmbed(1, new Dictionary<string, object> { ["image"] = "x" });
        var page = new Page();

        foreach (var update in updates)
        {
            page.Receive(update);
        }

        foreach (var update in page.Sent)
        {
            other.ApplyUpdate(update);
        }

        Assert.AreEqual(("a\nb", "a\nb", "a\nb"), (page.Text.Text, page.Value, text.ToString()));
    }

    // Two pages type a word each at the start of the empty room, keystroke by keystroke; the server passes the updates
    // on only every few keystrokes. Whoever's word comes second finds the other's at its caret, which stays before it.
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(7)]
    public void Edit_TwoPagesTypeAtOnePlace_EachWordStaysWhole(int every)
    {
        var room = new Room();
        var a = room.Open();
        var b = room.Open();
        string[] words = ["from-a ", "from-b "];

        for (var i = 0; i < words[0].Length; i++)
        {
            a.Type(words[0][i].ToString());
            b.Type(words[1][i].ToString());
            if (i % every == every - 1)
            {
                room.Deliver();
            }
        }

        room.Deliver();

        Assert.AreEqual((true, true), (a.Value == b.Value, a.Value is "from-a from-b " or "from-b from-a "));
    }

    // "ab" with the caret after "a"; another page inserts near it, then this page types: the caret moved as the text
    // area page moves it, and the keystroke goes where the caret is.
    [TestMethod]
    [DataRow(0, "b", "ba|b")]
    [DataRow(1, "b", "a|bb")]
    [DataRow(1, "a", "a|ab")]
    [DataRow(2, "b", "a|bb")]
    public void Edit_AfterARemoteInsertNearTheCaret_GoesWhereTheCaretIs(int at, string inserted, string expected)
    {
        var room = new Room();
        var page = room.Open();
        var other = room.Open();
        page.Type("ab");
        room.Deliver();
        page.Select(1, 1);

        other.Select(at, at);
        other.Type(inserted);
        room.Deliver();
        var shown = page.Value.Insert(page.End, "|");
        page.Type("X");
        room.Deliver();

        Assert.AreEqual((expected, expected.Replace("|", "X", StringComparison.Ordinal)), (shown, other.Value));
    }

    // "a|a" here, "aa|" there: an "a" typed here goes between the two, so the other's caret, at the end, moves past it,
    // and what the other types next comes last. Without the caret as a hint, the "a" would be the last, after the
    // other's caret, and the other's "Z" would land before it.
    [TestMethod]
    public void Edit_SameCharacterNextToAnIdenticalOne_GoesAtTheCaret()
    {
        var room = new Room();
        var page = room.Open();
        var other = room.Open();
        page.Type("aa");
        room.Deliver();
        page.Select(1, 1);
        other.Select(2, 2);

        page.Type("a");
        room.Deliver();
        other.Type("Z");
        room.Deliver();

        Assert.AreEqual(("aaaZ", "aaaZ"), (page.Value, other.Value));
    }

    [TestMethod]
    public void Show_ChangesAroundASelection_MoveItAsTheTextAreaPageDoes()
    {
        var room = new Room();
        var page = room.Open();
        var other = room.Open();
        page.Type("hello world");
        room.Deliver();
        page.Select(2, 7);

        other.Select(2, 2);
        other.Type("XY");
        other.Select(9, 9);
        other.Type("Z");
        room.Deliver();

        Assert.AreEqual(("heXYllo wZorld", 4, 9), (page.Value, page.Start, page.End));
    }

    // A page on its own document: the textarea's value and selection, kept as the Blazor page and room.js keep them.
    private sealed class Page
    {
        // The origin of what arrives from the others, as the provider applies it.
        private readonly object remote = new();

        public Page()
        {
            var doc = new YDoc();
            Text = new SharedText(doc);
            doc.Updated += (_, e) =>
            {
                if (e.Origin != remote)
                {
                    Sent.Add(e.Update.ToArray());
                }
            };
            Text.RemoteChange += (diff, changes) =>
            {
                Value = Value[..diff.Start] + diff.Inserted + Value[(diff.Start + diff.Removed)..];
                (Start, End) = TextDiff.MoveSelection(Start, End, changes);
            };
            Doc = doc;
        }

        public YDoc Doc { get; }

        public SharedText Text { get; }

        public List<byte[]> Sent { get; } = [];

        public string Value { get; private set; } = string.Empty;

        public int Start { get; private set; }

        public int End { get; private set; }

        public void Select(int start, int end) => (Start, End) = (start, end);

        // As the browser types: the selection is replaced, the caret goes after what was typed, then the input event.
        public void Type(string text)
        {
            Value = Value[..Start] + text + Value[End..];
            Start = End = Start + text.Length;
            Text.Edit(Value, End);
        }

        public void Receive(byte[] update)
        {
            Doc.ApplyUpdate(update, remote);
            Text.Show();
        }
    }

    // Deliver passes every update made since the last call on to the other pages, one by one, as the server does.
    private sealed class Room
    {
        private readonly List<(Page Page, int Delivered)> pages = [];

        public Page Open()
        {
            var page = new Page();
            pages.Add((page, 0));
            return page;
        }

        public void Deliver()
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var (author, delivered) = pages[i];
                foreach (var update in author.Sent.Skip(delivered).ToList())
                {
                    foreach (var (reader, _) in pages.Where(p => p.Page != author))
                    {
                        reader.Receive(update);
                    }
                }

                pages[i] = (author, author.Sent.Count);
            }
        }
    }
}
