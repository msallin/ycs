// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// Snapshot reads, as Yjs 13.6.32's <c>typeMapGetSnapshot</c>, <c>typeMapGetAllSnapshot</c> and
/// <c>typeListToArraySnapshot</c>: they only read (no transaction, no split at the snapshot), so an
/// item that was merged across the snapshot's boundary is seen whole, as in Yjs. Only
/// <see cref="YText.ToDelta"/> splits (<c>splitSnapshotAffectedStructs</c>, in a transaction).
/// </summary>
public partial class AbstractType
{
    /// <summary>
    /// Yjs' <c>typeMapGetSnapshot(parent, key, snapshot)</c>: the value of <paramref name="key"/> as of
    /// <paramref name="snapshot"/>, or <c>null</c> if the key was not visible then.
    /// </summary>
    public object GetSnapshot(string key, Snapshot snapshot) => TypeMapGetSnapshot(key, snapshot);

    /// <summary>
    /// Yjs' <c>typeMapGetAllSnapshot(parent, snapshot)</c>: every key/value pair as of <paramref name="snapshot"/>.
    /// </summary>
    public IDictionary<string, object> GetAllSnapshot(Snapshot snapshot)
    {
        var res = new Dictionary<string, object>();

        foreach (var kvp in _map)
        {
            var v = kvp.Value;

            while (!v.IsNull && (!snapshot.StateVector.TryGetValue(v.Id.Client, out var svClock) || v.Id.Clock >= svClock))
            {
                v = v.Left.AsItem;
            }

            if (!v.IsNull && v.IsVisible(snapshot))
            {
                res[kvp.Key] = ValueAt(v.Content, v.Length - 1);
            }
        }

        return res;
    }
}

public abstract partial class YArrayBase
{
    /// <summary>
    /// Yjs' <c>typeListToArraySnapshot(type, snapshot)</c>: the countable content visible in
    /// <paramref name="snapshot"/>, flattened as <c>ToArray()</c> flattens the live content.
    /// </summary>
    public IList<object> ToArraySnapshot(Snapshot snapshot)
    {
        var cs = new List<object>();
        ForEachSnapshot((value, index, arr) => cs.Add(value), snapshot);
        return cs;
    }
}
