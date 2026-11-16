namespace Ycs.Client;

/// <summary>
/// A textarea's binding to the <c>Y.Text</c> named <c>content</c>, synchronous both ways: the text as the page shows
/// it (<see cref="Text"/>), the user's edits in with <see cref="Edit(TextDiff)"/>, one transaction each, and what other
/// transactions changed out with <see cref="Show"/>. The Y.Text is kept plain text: a <c>\r</c>, an embed or formatting
/// another client puts in is removed. Not thread-safe: the owner calls it under the provider's gate.
/// </summary>
internal sealed class SharedText
{
    /// <summary>The name of the Y.Text, as the Yjs clients of the room name it.</summary>
    public const string Name = "content";

    // The origin of the user's own edits: the page shows them already.
    private readonly object typing = new();

    // The changes of the Y.Text not yet shown, in order: those of remote updates and of the cleanup after them.
    private readonly List<TextDiff> unshown = [];

    private readonly YDoc doc;
    private readonly YText text;

    public SharedText(YDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        this.doc = doc;
        text = doc.GetText(Name);
        Text = text.ToString();
        text.Observe(e =>
        {
            // The delta is computed now: ycs reuses the event once the handler returns.
            if (e.Transaction.Origin != typing)
            {
                TextDiff.FromDelta(e.Delta, unshown);
            }
        });
    }

    /// <summary>
    /// Remote updates changed the text. The first argument turns the old text into the new one (one range, for
    /// showing it); the second is what happened, in order, for carrying a caret through it with
    /// <see cref="TextDiff.MoveSelection"/>. The list is valid only during the call.
    /// </summary>
    public event Action<TextDiff, IReadOnlyList<TextDiff>>? RemoteChange;

    /// <summary>
    /// The text as the page shows it: the Y.Text's, whenever nothing is waiting to be shown. Kept, so that a
    /// keystroke costs no walk over the Y.Text.
    /// </summary>
    public string Text { get; private set; }

    /// <summary>
    /// The user changed the text that <see cref="Text"/> is to <paramref name="value"/>; the difference becomes one
    /// transaction, delete then insert. <paramref name="caret"/>: among equal characters, the edit is the one before it.
    /// </summary>
    public void Edit(string value, int caret = int.MaxValue) => Apply(TextDiff.Between(Text, value, caret), value);

    /// <summary>The user's edit as the page found it against <see cref="Text"/>: one replacement, one transaction.</summary>
    public void Edit(TextDiff change)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(change.Start + change.Removed, Text.Length, nameof(change));
        Apply(change, string.Concat(Text.AsSpan(0, change.Start), change.Inserted, Text.AsSpan(change.Start + change.Removed)));
    }

    /// <summary>
    /// Makes the Y.Text plain text again if another client put a <c>\r</c>, an embed or formatting in it (a textarea
    /// drops <c>\r</c>, and would then disagree on every position), in one transaction, and raises
    /// <see cref="RemoteChange"/> with what changed since the last call. The owner calls it after each remote update.
    /// </summary>
    public void Show()
    {
        // ycs keeps the string until the text changes, so asking again after nothing changed costs nothing.
        if (text.ToString().Contains('\r', StringComparison.Ordinal))
        {
            doc.Transact(text, static (text, _) =>
            {
                text.RemoveNonText();
                DeleteCarriageReturns(text);
            });
        }
        else
        {
            text.RemoveNonText();
        }

        if (unshown.Count == 0)
        {
            return;
        }

        var value = text.ToString();
        var diff = TextDiff.Between(Text, value);
        Text = value;
        RemoteChange?.Invoke(diff, unshown);
        unshown.Clear();
    }

    private static void Replace(YText text, TextDiff diff)
    {
        if (diff.Removed > 0)
        {
            text.Delete(diff.Start, diff.Removed);
        }

        if (diff.Inserted.Length > 0)
        {
            text.Insert(diff.Start, diff.Inserted);
        }
    }

    // From the end, so that every earlier index still holds; the text has nothing but strings, so its indexes are
    // the string's.
    private static void DeleteCarriageReturns(YText text)
    {
        var value = text.ToString();
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] == '\r')
            {
                text.Delete(i, 1);
            }
        }
    }

    private void Apply(TextDiff diff, string value)
    {
        if (diff.IsEmpty)
        {
            return;
        }

        Text = value;
        doc.Transact((text, diff), static (state, _) => Replace(state.text, state.diff), typing);
    }
}
