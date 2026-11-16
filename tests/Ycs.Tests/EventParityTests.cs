// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Regression + parity tests for <see cref="YEvent"/>: the <c>path</c> computation bug (it
/// counted preceding *items*, not their *content length*, and treated an empty-string map key
/// the same as "no key"), the <c>keys</c> computation bug (a key that doesn't need reporting used
/// to abort the whole per-key loop instead of just skipping that key), plus the additive surface
/// (top-level <c>Keys</c>/<c>Delta</c>, and <c>Adds</c>/<c>Deletes</c> now public) that a UI
/// binding (e.g. a Quill/ProseMirror adapter) needs to read the same shape Yjs exposes.
/// </summary>
[TestClass]
public class EventParityTests
{
    [TestMethod]
    public void TestPathCountsContentLengthNotItemCount()
    {
        // Regression test for a real bug in the ported GetPathTo(): plain (non-binary/type/doc)
        // values inserted together are packed into a *single* Item whose Length is the count of
        // packed values (see YArrayBase.InsertGenericsAfter's packJsonContent()). The path
        // computation must therefore accumulate `item.Length`, not increment by one per Item.
        var doc = new YDoc();
        var arr = doc.GetArray("array");

        // These three plain values are packed into ONE Item of Length == 3.
        arr.Insert(0, 1, 2, 3);

        var nestedMap = new YMap();
        arr.Insert(3, nestedMap);

        IReadOnlyList<PathSegment> capturedPath = null;
        arr.ObserveDeep((deepEvents, _) => { capturedPath = deepEvents[0].Path; });

        nestedMap["x"] = 1;

        Assert.IsNotNull(capturedPath);
        CollectionAssert.AreEqual(new[] { new PathSegment(null, 3) }, capturedPath.ToList());
    }

    [TestMethod]
    public void TestPathTreatsEmptyStringKeyAsMapKey()
    {
        // An empty string is a legal YMap key distinct from "no key" (array-ish). The path
        // computation used to conflate the two via string.IsNullOrEmpty().
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var nested = new YMap();
        map[string.Empty] = nested;

        IReadOnlyList<PathSegment> capturedPath = null;
        map.ObserveDeep((deepEvents, _) => { capturedPath = deepEvents[0].Path; });

        nested["x"] = 1;

        Assert.IsNotNull(capturedPath);
        CollectionAssert.AreEqual(new[] { new PathSegment(string.Empty, 0) }, capturedPath.ToList());
    }

    [TestMethod]
    public void TestKeysDoesNotAbortOnAddThenDeleteWithinSameTransaction()
    {
        // Regression test: a key that is added and then deleted within the same transaction has
        // nothing to report (Yjs' `keys` getter does `return` from the forEach callback for just
        // that key, i.e. "skip"). The ported version used `break`, which silently dropped every
        // other changed key in the same event depending on Dictionary/HashSet iteration order.
        var doc = new YDoc();
        var map = doc.GetMap("map");

        // An event's changes are computed in its handler (afterwards the transaction has ended).
        YMapEvent evt = null;
        map.Observe(e =>
        {
            evt = e as YMapEvent;
            _ = evt.Changes;
        });

        doc.Transact(_ =>
        {
            map["a"] = 1;
            map.Remove("a"); // Added + deleted in the same transaction => nothing to report for "a".
            map["b"] = 1;
            map["c"] = 1;
            map["d"] = 1;
        });

        Assert.IsNotNull(evt);
        var keys = evt.Changes.Keys;

        Assert.IsFalse(keys.ContainsKey("a"));
        Assert.AreEqual(3, keys.Count, "Keys 'b', 'c' and 'd' must all be reported regardless of iteration order.");
        foreach (var k in new[] { "b", "c", "d" })
        {
            Assert.IsTrue(keys.ContainsKey(k));
            Assert.AreEqual(KeyChangeAction.Add, keys[k].Action);
        }
    }

    [TestMethod]
    public void TestTopLevelKeysPropertyMatchesChangesKeys()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        // An event's changes are computed in its handler (afterwards the transaction has ended).
        YMapEvent evt = null;
        map.Observe(e =>
        {
            evt = e as YMapEvent;
            _ = evt.Changes;
        });

        map["a"] = 1;

        Assert.IsNotNull(evt);
        Assert.AreSame(evt.Changes.Keys, evt.Keys, "YEvent.Keys must be the very same cached map exposed via Changes.Keys.");
        Assert.AreEqual(KeyChangeAction.Add, evt.Keys["a"].Action);
    }

    [TestMethod]
    public void TestTopLevelDeltaMatchesChangesDeltaForArrayEvents()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");

        YArrayEvent evt = null;
        arr.Observe(e =>
        {
            evt = e as YArrayEvent;
            _ = evt.Changes;
        });

        arr.Insert(0, 1, 2, 3);

        Assert.IsNotNull(evt);
        Assert.AreSame(evt.Changes.Delta, evt.Delta, "YEvent.Delta must default to Changes.Delta for non-YText events.");
        Assert.AreEqual(1, evt.Delta.Count);
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, evt.Delta[0] is Delta.InsertItems items ? items.Items.Select(i => i.ToObject()).ToList() : null);
    }

    [TestMethod]
    public void TestYTextEventChangesDeltaIsTheTextDelta()
    {
        // As in Yjs, a text event's changes.delta is its Quill-style delta itself (YTextEvent#changes).
        var doc = new YDoc();
        var text = doc.GetText("text");

        YTextEvent evt = null;
        text.Observe(e =>
        {
            evt = e as YTextEvent;
            _ = evt.Changes;
            _ = evt.Delta;
        });

        text.Insert(0, "abc");

        Assert.IsNotNull(evt);
        Assert.AreSame(evt.Changes.Delta, evt.Delta);
        Assert.AreEqual(1, evt.Delta.Count);
        Assert.AreEqual("abc", evt.Delta[0].Inserted());
        Assert.IsNull(evt.Delta[0].Attributes);
    }

    [TestMethod]
    public void TestAddsAndDeletesArePublic()
    {
        // Yjs exposes `event.adds(struct)`/`event.deletes(struct)` publicly for UI bindings that
        // need to distinguish "added in this transaction" from "already existed". They used to be
        // `internal` in this port. Changes.Added/Deleted are only populated for list-shaped
        // events (array/text), so exercise this via a YArray insert.
        var doc = new YDoc();
        var arr = doc.GetArray("array");

        // We can't get the raw Item back out of a public API, so just prove the methods compile
        // and run as *public* members by invoking them through the event's own added set, in the
        // handler (afterwards the transaction has ended).
        (bool Adds, bool Deletes)? result = null;
        arr.Observe(e =>
        {
            var addedItem = e.Changes.Added.Single();
            result = (e.Adds(addedItem), e.Deletes(addedItem));
        });

        arr.Insert(0, "x");

        Assert.AreEqual((true, false), result);
    }
}
