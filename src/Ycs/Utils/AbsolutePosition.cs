// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable enable

namespace Ycs;

/// <summary>
/// A position as an index in a type, at the document's state of the moment; what
/// <see cref="RelativePosition.TryResolve"/> hands back. It moves with edits only if resolved again.
/// </summary>
/// <param name="Type">The type the index is in.</param>
/// <param name="Index">The index in <paramref name="Type"/>.</param>
/// <param name="Assoc">The association of the relative position it came from.</param>
public readonly record struct AbsolutePosition(AbstractType Type, int Index, int Assoc = 0)
{
    internal static bool TryResolve(RelativePosition rpos, YDoc doc, bool followUndoneDeletions, out AbsolutePosition position)
    {
        var store = doc.Store;
        var rightId = rpos.Item;
        var typeId = rpos.TypeId;
        var tName = rpos.RootName;
        var assoc = rpos.Assoc;
        int index = 0;
        AbstractType type;

        if (rightId != null)
        {
            if (store.GetState(rightId.Value.Client) <= rightId.Value.Clock)
            {
                position = default;
                return false;
            }

            var res = followUndoneDeletions ? store.FollowRedone(rightId.Value) : GetItemWithOffset(store, rightId.Value);
            var right = res.item.AsItem;
            if (right.IsNull)
            {
                position = default;
                return false;
            }

            type = (AbstractType)right.Parent!;

            if (!type.ItemDeleted)
            {
                // Adjust position based on the left assotiation, if necessary.
                index = (right.Deleted || !right.Countable) ? 0 : (res.diff + (assoc >= 0 ? 0 : 1));
                var n = right.Left.AsItem;
                while (!n.IsNull)
                {
                    if (!n.Deleted && n.Countable)
                    {
                        index += n.Length;
                    }

                    n = n.Left.AsItem;
                }
            }
        }
        else
        {
            if (tName != null)
            {
                type = doc.Get<AbstractType>(tName);
            }
            else if (typeId == null)
            {
                // default(RelativePosition) names nothing.
                position = default;
                return false;
            }
            else
            {
                if (store.GetState(typeId.Value.Client) <= typeId.Value.Clock)
                {
                    // Type does not exist yet.
                    position = default;
                    return false;
                }

                var res = followUndoneDeletions ? store.FollowRedone(typeId.Value) : GetItemWithOffset(store, typeId.Value);
                var item = res.item.AsItem;
                if (!item.IsNull && item.Content is ContentType content)
                {
                    type = content.Type;
                }
                else
                {
                    // Struct is garbage collected.
                    position = default;
                    return false;
                }
            }

            index = assoc >= 0 ? type.Length : 0;
        }

        position = new AbsolutePosition(type, index, assoc);
        return true;
    }

    /// <summary>
    /// The non-redone-following counterpart to <see cref="StructStore.FollowRedone"/>, used when
    /// <c>followUndoneDeletions</c> is <c>false</c>.
    /// </summary>
    private static (ItemRef item, int diff) GetItemWithOffset(StructStore store, ID id)
    {
        var item = store.Find(id);
        return (item, (int)(id.Clock - item.Id.Clock));
    }
}
