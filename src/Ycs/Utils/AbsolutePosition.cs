// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Diagnostics;

namespace Ycs;

/// <summary>
/// Mirrors Yjs 13.6.32's <c>AbsolutePosition</c> (src/utils/RelativePosition.js). Public - rather
/// than the <c>internal</c> it started as - for the same reason <see cref="RelativePosition"/> is:
/// it is what <see cref="TryCreateFromRelativePosition"/> hands back to callers outside this
/// assembly.
/// </summary>
public class AbsolutePosition
{
    public readonly AbstractType Type;
    public readonly int Index;
    public readonly int Assoc;

    public AbsolutePosition(AbstractType type, int index, int assoc = 0)
    {
        Type = type;
        Index = index;
        Assoc = assoc;
    }

    /// <summary>
    /// Yjs parity: <c>createAbsolutePosition(type, index, assoc)</c>.
    /// </summary>
    public static AbsolutePosition CreateAbsolutePosition(AbstractType type, int index, int assoc = 0)
        => new AbsolutePosition(type, index, assoc);

    /// <summary>
    /// Yjs parity: <c>createAbsolutePositionFromRelativePosition(rpos, doc, followUndoneDeletions)</c>.
    /// Alias for <see cref="TryCreateFromRelativePosition"/>, kept under the Yjs name for
    /// discoverability.
    /// </summary>
    public static AbsolutePosition CreateAbsolutePositionFromRelativePosition(RelativePosition rpos, YDoc doc, bool followUndoneDeletions = true)
        => TryCreateFromRelativePosition(rpos, doc, followUndoneDeletions);

    /// <summary>
    /// Transform a relative position into an absolute position.
    /// <br/>
    /// If you want to share the relative position with other users, set <paramref name="followUndoneDeletions"/>
    /// to <c>false</c> to get consistent results across all clients. When calculating the absolute
    /// position, we try by default to follow "undone deletions" - this yields better results for the
    /// user who performed the undo, but only for that user, since other users don't know which
    /// operations recreated a deleted range of content. See https://github.com/yjs/yjs/issues/638
    /// for more.
    /// </summary>
    public static AbsolutePosition TryCreateFromRelativePosition(RelativePosition rpos, YDoc doc, bool followUndoneDeletions = true)
    {
        var store = doc.Store;
        var rightId = rpos.Item;
        var typeId = rpos.TypeId;
        var tName = rpos.TName;
        var assoc = rpos.Assoc;
        int index = 0;
        AbstractType type;

        if (rightId != null)
        {
            if (store.GetState(rightId.Value.Client) <= rightId.Value.Clock)
            {
                return null;
            }

            var res = followUndoneDeletions ? store.FollowRedone(rightId.Value) : GetItemWithOffset(store, rightId.Value);
            var right = res.item as Item;
            if (right == null)
            {
                return null;
            }

            type = right.Parent as AbstractType;
            Debug.Assert(type != null);

            if (type._item == null || !type._item.Deleted)
            {
                // Adjust position based on the left assotiation, if necessary.
                index = (right.Deleted || !right.Countable) ? 0 : (res.diff + (assoc >= 0 ? 0 : 1));
                var n = right.Left as Item;
                while (n != null)
                {
                    if (!n.Deleted && n.Countable)
                    {
                        index += n.Length;
                    }

                    n = n.Left as Item;
                }
            }
        }
        else
        {
            if (tName != null)
            {
                type = doc.Get<AbstractType>(tName);
            }
            else if (typeId != null)
            {
                if (store.GetState(typeId.Value.Client) <= typeId.Value.Clock)
                {
                    // Type does not exist yet.
                    return null;
                }

                var res = followUndoneDeletions ? store.FollowRedone(typeId.Value) : GetItemWithOffset(store, typeId.Value);
                var item = res.item as Item;
                if (item != null && item.Content is ContentType)
                {
                    type = (item.Content as ContentType).Type;
                }
                else
                {
                    // Struct is garbage collected.
                    return null;
                }
            }
            else
            {
                throw new Exception();
            }

            index = assoc >= 0 ? type.Length : 0;
        }

        return new AbsolutePosition(type, index, assoc);
    }

    /// <summary>
    /// Yjs parity: <c>getItemWithOffset(store, id)</c> - the non-redone-following counterpart to
    /// <see cref="StructStore.FollowRedone"/>, used when <c>followUndoneDeletions</c> is <c>false</c>.
    /// </summary>
    private static (AbstractStruct item, int diff) GetItemWithOffset(StructStore store, ID id)
    {
        var item = store.Find(id);
        return (item, (int)(id.Clock - item.Id.Clock));
    }
}
