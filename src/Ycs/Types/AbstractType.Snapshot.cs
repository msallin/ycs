// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Ycs
{
    /// <summary>
    /// Snapshot-aware type helpers, ported from Yjs 13.6.32's types/AbstractType.js.
    /// <br/>
    /// Yjs' <c>isVisible(item, snapshot)</c> is already faithfully ported as the internal
    /// <see cref="Item.IsVisible(Snapshot)"/> (see Structs/Item.cs) - it is not re-added here because
    /// Yjs itself does not export it from its public API (it is <c>@protected</c>/internal-only there
    /// too; see yjs/src/index.js, which does not list it alongside the sibling functions below).
    /// <br/>
    /// <c>typeMapGetSnapshot(parent, key, snapshot)</c> was likewise already ported, as the
    /// <c>protected</c> <see cref="AbstractType.TypeMapGetSnapshot"/> (Types/AbstractType.cs) - it just
    /// had no public entry point. <see cref="GetSnapshot"/> below is that entry point.
    /// <br/>
    /// This file adds one partial-class block per type that needed a new member. Both
    /// <see cref="AbstractType"/> and <see cref="YArrayBase"/> are declared <c>partial</c> already
    /// (Types/AbstractType.cs, Types/YArrayBase.cs), so their new members here are inherited by
    /// <c>YMap</c>/<c>YArray</c>/<c>YText</c> without needing to touch those files at all - which
    /// matters because <c>YMap</c> itself is declared as a plain (non-<c>partial</c>) class in
    /// Types/YMap.cs (only the unrelated <c>YMapEvent</c> in that file was made partial by the prep
    /// commit for this wave, apparently a copy/paste slip - see the note in Utils/YDoc.Snapshot.cs for
    /// the same slip on <c>YDoc</c>). <c>YMap.cs</c> is not in this task's file-ownership list, so
    /// rather than edit it, snapshot-aware map helpers are added to the shared <see cref="AbstractType"/>
    /// base instead, which every type - including <c>YMap</c> - already inherits from.
    /// </summary>
    public partial class AbstractType
    {
        /// <summary>
        /// Yjs parity: <c>typeMapGetSnapshot(parent, key, snapshot)</c>. The value of <paramref name="key"/>
        /// as of <paramref name="snapshot"/>, or <c>null</c> if the key did not exist (or was not yet
        /// visible) at that point. Thin public wrapper around the existing
        /// <see cref="TypeMapGetSnapshot"/>, run inside a transaction that first splits any struct
        /// straddling the snapshot's state-vector boundary (see the class remarks on
        /// <see cref="SplitForSnapshot"/> for why that split is required).
        /// </summary>
        public object GetSnapshot(string key, Snapshot snapshot)
        {
            object result = null;
            SplitForSnapshot(snapshot, () => result = TypeMapGetSnapshot(key, snapshot));
            return result;
        }

        /// <summary>
        /// Yjs parity: <c>typeMapGetAllSnapshot(parent, snapshot)</c>. All key/value pairs as of
        /// <paramref name="snapshot"/>.
        /// </summary>
        public IDictionary<string, object> GetAllSnapshot(Snapshot snapshot)
        {
            var res = new Dictionary<string, object>();

            SplitForSnapshot(snapshot, () =>
            {
                foreach (var kvp in _map)
                {
                    var v = kvp.Value;

                    while (v != null && (!snapshot.StateVector.TryGetValue(v.Id.Client, out var svClock) || v.Id.Clock >= svClock))
                    {
                        v = v.Left as Item;
                    }

                    if (v != null && v.IsVisible(snapshot))
                    {
                        res[kvp.Key] = v.Content.GetContent()[v.Length - 1];
                    }
                }
            });

            return res;
        }

        /// <summary>
        /// A struct that was a single insert at write time can straddle a snapshot's state-vector
        /// boundary once later edits let the transaction-cleanup pass merge it with its (now
        /// same-client, contiguous) neighbours - e.g. inserting "y","z" and later, in a separate
        /// transact(), "w" right after them normally leaves three separate structs, but cleanup merges
        /// them into one <c>[y,z,w]</c> struct spanning clocks that a snapshot taken between the two
        /// inserts should see only part of. Per-item visibility checks
        /// (<see cref="Item.IsVisible(Snapshot)"/>) test the *whole* struct's id/length against the
        /// snapshot, so a merged struct like that is wrongly treated as either entirely visible or
        /// entirely invisible.
        /// <br/>
        /// Yjs's own <c>typeListToArraySnapshot</c>/<c>typeMapGetSnapshot</c>/<c>typeMapGetAllSnapshot</c>
        /// callers route around this by calling <c>splitSnapshotAffectedStructs(transaction, snapshot)</c>
        /// inside a <c>transact()</c> before reading (see e.g. YText.js' <c>toDelta</c>, and this port's
        /// own faithful copy of that pattern in <see cref="YText.ToDelta"/>) - it splits every struct
        /// that straddles the snapshot's boundary into two clean ones first. This helper does the same:
        /// <see cref="Transaction.SplitSnapshotAffectedStructs"/> already exists in this port (unused
        /// until now) for exactly this purpose.
        /// </summary>
        private void SplitForSnapshot(Snapshot snapshot, System.Action read)
        {
            Doc.Transact(tr =>
            {
                Transaction.SplitSnapshotAffectedStructs(tr, snapshot);
                read();
            });
        }
    }

    public abstract partial class YArrayBase
    {
        /// <summary>
        /// Yjs parity: <c>typeListToArraySnapshot(type, snapshot)</c>. All countable, visible-as-of-
        /// <paramref name="snapshot"/> content, flattened into a single list the way <c>ToArray()</c>
        /// flattens the live content. Reuses the existing (previously unused) <see cref="ForEachSnapshot"/>
        /// for the walk; see <see cref="AbstractType.SplitForSnapshot"/> for why the read has to happen
        /// inside a transaction that splits snapshot-straddling structs first.
        /// </summary>
        public IList<object> ToArraySnapshot(Snapshot snapshot)
        {
            var cs = new List<object>();

            Doc.Transact(tr =>
            {
                Transaction.SplitSnapshotAffectedStructs(tr, snapshot);
                ForEachSnapshot((value, index, arr) => cs.Add(value), snapshot);
            });

            return cs;
        }
    }
}
