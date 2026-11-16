// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Snapshot reads, as Yjs 13.6.32's <c>typeMapGetSnapshot</c>, <c>typeMapGetAllSnapshot</c> and
/// <c>typeListToArraySnapshot</c> (the latter is <c>YArrayBase.ContentAtSnapshot</c>): they only read (no transaction,
/// no split at the snapshot), so an item that was merged across the snapshot's boundary is seen whole, as in Yjs. Only
/// <see cref="YText.ToDelta"/> splits (<c>splitSnapshotAffectedStructs</c>, in a transaction).
/// </summary>
public abstract partial class YType
{
    /// <summary>The value of <paramref name="key"/> as of <paramref name="snapshot"/>, as stored; false if the key was not visible then.</summary>
    internal bool TryMapGetSnapshot(string key, Snapshot snapshot, out object? value)
    {
        value = null;
        if (!_map.TryGetValue(key, out var v))
        {
            return false;
        }

        v = MapItemAt(v, snapshot);
        if (v.IsNull || !v.IsVisible(snapshot))
        {
            return false;
        }

        value = ValueAt(v.Content, v.Length - 1);
        return true;
    }

    /// <summary>Every key/value pair as of <paramref name="snapshot"/>.</summary>
    internal Dictionary<string, YValue> MapAtSnapshot(Snapshot snapshot)
    {
        var res = new Dictionary<string, YValue>();

        foreach (var kvp in _map)
        {
            var v = MapItemAt(kvp.Value, snapshot);
            if (!v.IsNull && v.IsVisible(snapshot))
            {
                res[kvp.Key] = YValue.FromStored(ValueAt(v.Content, v.Length - 1));
            }
        }

        return res;
    }

    // The last item of a key's chain that existed when the snapshot was taken.
    private static ItemRef MapItemAt(ItemRef v, Snapshot snapshot)
    {
        while (!v.IsNull && (!snapshot.StateVector.TryGetValue(v.Id.Client, out var svClock) || v.Id.Clock >= svClock))
        {
            v = v.Left.AsItem;
        }

        return v;
    }
}
