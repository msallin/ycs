#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public readonly partial struct ItemView
{
    private const int MaxListed = 4;

    /// <summary>
    /// Compares the views of the structs an event reaches with the structs: the first <see cref="MaxListed"/> of its
    /// target's lists when it is made (<paramref name="changes"/> null), the added and deleted ones of its changes when
    /// they are collected. A test of a long list stays linear in its edits.
    /// </summary>
    internal static void CheckEvent(YEvent e, YChanges changes)
    {
        var doc = e.Target.Doc;
        if (changes != null)
        {
            foreach (var view in changes.Added.Concat(changes.Deleted))
            {
                var str = doc.Store.Find(view.Id).ToStruct();
                if (str.Id.Equals(view.Id))
                {
                    Check(doc, str);
                }
            }

            return;
        }

        var target = e.Target;
        var listed = 0;
        for (var n = target._start; !n.IsNull && listed++ < MaxListed; n = n.Right.AsItem)
        {
            Check(doc, n.ToStruct());
        }

        foreach (var value in target._map.Values)
        {
            listed = 0;
            for (var n = value; !n.IsNull && listed++ < MaxListed; n = n.Left.AsItem)
            {
                Check(doc, n.ToStruct());
            }
        }
    }

    /// <summary>
    /// Throws when a view of <paramref name="str"/> (made from it, and made from its id alone) reads anything other than
    /// the struct does. A struct that left the store is skipped: its view reads the struct that holds its id now.
    /// </summary>
    internal static void Check(YDoc doc, AbstractStruct str)
    {
        if (IsOut(str))
        {
            return;
        }

        Check(Of(doc, str), str);
        Check(At(doc, str.Id), str);
    }

    private static void Check(ItemView view, AbstractStruct str)
    {
        void Same<T>(string member, T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"The view of {str.Id.Client}:{str.Id.Clock} reads {member} {actual}, its struct {expected}.");
            }
        }

        var item = str as Item;
        Same("Id", str.Id, view.Id);
        Same("Length", str.Length, view.Length);
        Same("LastId", new ID(str.Id.Client, str.Id.Clock + str.Length - 1), view.LastId);
        Same("Deleted", str.Deleted, view.Deleted);
        Same("Kind", item != null ? (ItemKind)item.Content.Ref : str is GC ? ItemKind.GC : ItemKind.Skip, view.Kind);
        Same("Countable", item?.Countable ?? false, view.Countable);
        Same("Keep", item?.Keep ?? false, view.Keep);
        Same("LeftOrigin", item?.LeftOrigin, view.LeftOrigin);
        Same("RightOrigin", item?.RightOrigin, view.RightOrigin);
        Same("Redone", item?.Redone, view.Redone);
        Same("ParentSub", item?.ParentSub, view.ParentSub);
        Same("Parent", item?.Parent as AbstractType, view.Parent);
        Same("Left", (item?.Left)?.Id, view.Left?.Id);
        Same("Right", (item?.Right)?.Id, view.Right?.Id);
        Same("Left length", (item?.Left)?.Length, view.Left?.Length);
        Same("Right length", (item?.Right)?.Length, view.Right?.Length);
        var expected = item?.Content.GetContent() ?? Array.Empty<object>();
        var content = view.Content;
        Same("Content count", expected.Count, content.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Same("Content", YValue.FromStored(expected[i]), YValue.FromStored(content[i]));
        }
    }
}
