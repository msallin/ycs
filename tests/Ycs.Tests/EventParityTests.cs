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
        arr.Insert(0, new object[] { 1, 2, 3 });

        var nestedMap = new YMap();
        arr.Insert(3, new object[] { nestedMap });

        IReadOnlyCollection<object> capturedPath = null;
        arr.ObserveDeep((s, e) => { capturedPath = e.Events[0].Path; });

        nestedMap.Set("x", 1);

        Assert.IsNotNull(capturedPath);
        CollectionAssert.AreEqual(new object[] { 3 }, capturedPath.ToList());
    }

    [TestMethod]
    public void TestPathTreatsEmptyStringKeyAsMapKey()
    {
        // An empty string is a legal YMap key distinct from "no key" (array-ish). The path
        // computation used to conflate the two via string.IsNullOrEmpty().
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var nested = new YMap();
        map.Set(string.Empty, nested);

        IReadOnlyCollection<object> capturedPath = null;
        map.ObserveDeep((s, e) => { capturedPath = e.Events[0].Path; });

        nested.Set("x", 1);

        Assert.IsNotNull(capturedPath);
        CollectionAssert.AreEqual(new object[] { string.Empty }, capturedPath.ToList());
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
        map.EventHandler += (s, e) =>
        {
            evt = e.Event as YMapEvent;
            _ = evt.Changes;
        };

        doc.Transact(_ =>
        {
            map.Set("a", 1);
            map.Delete("a"); // Added + deleted in the same transaction => nothing to report for "a".
            map.Set("b", 1);
            map.Set("c", 1);
            map.Set("d", 1);
        });

        Assert.IsNotNull(evt);
        var keys = evt.Changes.Keys;

        Assert.IsFalse(keys.ContainsKey("a"));
        Assert.AreEqual(3, keys.Count, "Keys 'b', 'c' and 'd' must all be reported regardless of iteration order.");
        foreach (var k in new[] { "b", "c", "d" })
        {
            Assert.IsTrue(keys.ContainsKey(k));
            Assert.AreEqual(ChangeAction.Add, keys[k].Action);
        }
    }

    [TestMethod]
    public void TestTopLevelKeysPropertyMatchesChangesKeys()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        // An event's changes are computed in its handler (afterwards the transaction has ended).
        YMapEvent evt = null;
        map.EventHandler += (s, e) =>
        {
            evt = e.Event as YMapEvent;
            _ = evt.Changes;
        };

        map.Set("a", 1);

        Assert.IsNotNull(evt);
        Assert.AreSame(evt.Changes.Keys, evt.Keys, "YEvent.Keys must be the very same cached map exposed via Changes.Keys.");
        Assert.AreEqual(ChangeAction.Add, evt.Keys["a"].Action);
    }

    [TestMethod]
    public void TestTopLevelDeltaMatchesChangesDeltaForArrayEvents()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");

        YArrayEvent evt = null;
        arr.EventHandler += (s, e) =>
        {
            evt = e.Event as YArrayEvent;
            _ = evt.Changes;
        };

        arr.Insert(0, new object[] { 1, 2, 3 });

        Assert.IsNotNull(evt);
        Assert.AreSame(evt.Changes.Delta, evt.Delta, "YEvent.Delta must default to Changes.Delta for non-YText events.");
        Assert.AreEqual(1, evt.Delta.Count);
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, (evt.Delta[0].Insert as IList<object>).ToList());
    }

    [TestMethod]
    public void TestYTextEventDeltaOverridesTheBaseDeltaShortcut()
    {
        // YText's own Delta is a Quill-style, attribute-aware computation -- structurally
        // different from (and must NOT be the same object as) the plain insert/retain/delete
        // ops in Changes.Delta, exactly as in Yjs (YTextEvent.delta overrides YEvent.delta).
        //
        // Note: this deliberately inserts *without* formatting attributes. `Changes.Delta` (the
        // base, non-overridden computation) walks every Item in the changed range and calls
        // `Content.GetContent()` on it; `ContentFormat.GetContent()` throws `NotImplementedException`
        // in this port (src/Ycs/Structs/ContentFormat.cs, outside this task's file ownership --
        // real Yjs's equivalent returns `[]`). Exercising that codepath is a separate, pre-existing
        // bug tracked out-of-band; this test sticks to plain text so it only proves the override.
        var doc = new YDoc();
        var text = doc.GetText("text");

        YTextEvent evt = null;
        text.EventHandler += (s, e) =>
        {
            evt = e.Event as YTextEvent;
            _ = evt.Changes;
            _ = evt.Delta;
        };

        text.Insert(0, "abc");

        Assert.IsNotNull(evt);
        Assert.AreNotSame(evt.Changes.Delta, evt.Delta);
        Assert.AreEqual(1, evt.Delta.Count);
        Assert.AreEqual("abc", evt.Delta[0].Insert);
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
        arr.EventHandler += (s, e) =>
        {
            var addedItem = e.Event.Changes.Added.Single();
            result = (e.Event.Adds(addedItem), e.Event.Deletes(addedItem));
        };

        arr.Insert(0, new object[] { "x" });

        Assert.AreEqual((true, false), result);
    }
}
