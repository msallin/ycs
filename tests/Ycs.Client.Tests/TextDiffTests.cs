namespace Ycs.Client.Tests;

[TestClass]
public class TextDiffTests
{
    [TestMethod]
    [DataRow("abc", "abXc", 2, 0, "X")]
    [DataRow("hello", "help", 3, 2, "p")]
    [DataRow("aa", "aaa", 2, 0, "a")]
    [DataRow("abc", "", 0, 3, "")]
    [DataRow("", "abc", 0, 0, "abc")]
    [DataRow("a😀b", "a😁b", 1, 2, "😁")]
    [DataRow("a😀b", "a🨀b", 1, 2, "🨀")]
    public void Between_TwoTexts_IsTheReplacedRange(string before, string after, int start, int removed, string inserted)
    {
        var diff = TextDiff.Between(before, after);

        Assert.AreEqual(new TextDiff(start, removed, inserted), diff);
    }

    [TestMethod]
    public void Between_SameText_IsEmpty() => Assert.IsTrue(TextDiff.Between("same", "same").IsEmpty);

    // The caret is where it is in the new text, as the textarea's selectionEnd after the input.
    [TestMethod]
    [DataRow("aa", "aaa", 1, 0, 0, "a")]
    [DataRow("aa", "aaa", 2, 1, 0, "a")]
    [DataRow("aa", "aaa", 3, 2, 0, "a")]
    [DataRow("abb", "ab", 1, 1, 1, "")]
    [DataRow("abb", "ab", 2, 2, 1, "")]
    [DataRow("from-b", "ffrom-b", 1, 0, 0, "f")]
    [DataRow("hello", "hXo", 2, 1, 3, "X")]
    [DataRow("abab", "ababab", 2, 0, 0, "ab")]
    [DataRow("abab", "ababab", 4, 2, 0, "ab")]
    [DataRow("😀😀", "😀😀😀", 2, 0, 0, "😀")]
    [DataRow("😀😀", "😀😀😀", 4, 2, 0, "😀")]
    [DataRow("😀", "😀😀", 3, 0, 0, "😀")]
    [DataRow("😀😀", "😀", 0, 0, 2, "")]
    [DataRow("x", "xy", 99, 1, 0, "y")]
    public void Between_CaretHint_PlacesTheEditBeforeTheCaret(string before, string after, int caret, int start, int removed, string inserted)
    {
        var diff = TextDiff.Between(before, after, caret);

        Assert.AreEqual(new TextDiff(start, removed, inserted), diff);
    }

    // In "hello world": index, the change, whether the position follows the character after it, the result.
    [TestMethod]
    [DataRow(5, 2, 0, "XY", false, 7)]
    [DataRow(5, 5, 0, "XY", false, 5)]
    [DataRow(5, 5, 0, "XY", true, 7)]
    [DataRow(5, 8, 0, "XY", true, 5)]
    [DataRow(5, 3, 5, "", false, 3)]
    [DataRow(5, 3, 5, "QQQQ", false, 3)]
    [DataRow(5, 3, 5, "QQQQ", true, 7)]
    [DataRow(5, 1, 2, "", false, 3)]
    [DataRow(5, 1, 4, "Z", false, 1)]
    [DataRow(5, 5, 3, "", false, 5)]
    [DataRow(5, 5, 3, "", true, 5)]
    [DataRow(5, 6, 3, "", false, 5)]
    public void Move_OneChange_IsWhereThePositionGoes(int index, int start, int removed, string inserted, bool after, int expected)
    {
        var moved = new TextDiff(start, removed, inserted).Move(index, after);

        Assert.AreEqual(expected, moved);
    }

    // The selection [2, 7) or the caret 5 of "hello world", and one change of another user.
    [TestMethod]
    [DataRow(5, 5, 5, 0, "XY", 5, 5)]
    [DataRow(5, 5, 2, 0, "XY", 7, 7)]
    [DataRow(2, 7, 2, 0, "XY", 4, 9)]
    [DataRow(2, 7, 4, 0, "XY", 2, 9)]
    [DataRow(2, 7, 7, 0, "XY", 2, 7)]
    [DataRow(2, 7, 5, 4, "", 2, 5)]
    [DataRow(2, 7, 1, 8, "", 1, 1)]
    public void MoveSelection_ChangeOfAnother_IsAsTheTextAreaPageMovesIt(int start, int end, int at, int removed, string inserted, int newStart, int newEnd)
    {
        var moved = TextDiff.MoveSelection(start, end, [new TextDiff(at, removed, inserted)]);

        Assert.AreEqual((newStart, newEnd), moved);
    }

    [TestMethod]
    public void MoveSelection_SeveralChanges_GoesThroughThemInOrder()
    {
        TextDiff[] changes = [new(0, 0, "ab"), new(7, 0, "Z"), new(9, 2, string.Empty)];

        var moved = TextDiff.MoveSelection(5, 5, changes);

        Assert.AreEqual((7, 7), moved);
    }

    [TestMethod]
    public void FromDelta_RetainsInsertsDeletesAndEmbeds_AreChangesInOrder()
    {
        var changes = new List<TextDiff>();
        Delta[] delta =
        [
            new() { Retain = 2 },
            new() { Insert = "ab" },
            new() { Delete = 1 },
            new() { Retain = 3, Attributes = new Dictionary<string, object> { ["bold"] = true } },
            new() { Insert = new Dictionary<string, object> { ["image"] = "x" } },
        ];

        TextDiff.FromDelta(delta, changes);

        CollectionAssert.AreEqual(new[] { new TextDiff(2, 0, "ab"), new TextDiff(4, 1, string.Empty), new TextDiff(7, 0, "\uFFFC") }, changes);
    }

    // A null embed (another peer's { insert: null }) is Delta.NullEmbed, not a missing insert: one placeholder, so the
    // "c" after it stays at index 2, where a skipped null embed would have left it one off (ycs' E4 fix).
    [TestMethod]
    public void FromDelta_NullEmbed_IsOnePlaceholderAndLaterTextDoesNotShift()
    {
        var changes = new List<TextDiff>();
        Delta[] delta = [new() { Insert = "ab" }, new() { Insert = Delta.NullEmbed }, new() { Retain = 1 }, new() { Insert = "c" }];

        TextDiff.FromDelta(delta, changes);

        CollectionAssert.AreEqual(new[] { new TextDiff(0, 0, "ab"), new TextDiff(2, 0, "\uFFFC"), new TextDiff(4, 0, "c") }, changes);
    }
}
