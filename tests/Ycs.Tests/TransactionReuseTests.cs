// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A document reuses its transaction objects: these tests hold the lifecycle to Yjs' semantics
/// (nesting, transactions that observers start, exceptions) and check that nothing of an ended
/// transaction leaks into the next one.
/// </summary>
[TestClass]
public class TransactionReuseTests
{
    [TestMethod]
    public void Transact_OneAfterAnother_ReusesTheTransactionObject()
    {
        var doc = new YDoc();
        var seen = new List<Transaction>();
        doc.AfterTransaction += (_, tr) => seen.Add(tr);

        doc.GetText("t").Insert(0, "a");
        doc.GetText("t").Insert(1, "b");

        Assert.AreSame(seen[0], seen[1]);
    }

    [TestMethod]
    public void Transact_EndedTransactionRead_Throws()
    {
        var doc = new YDoc();
        Transaction kept = null;
        doc.Transact(tr => kept = tr, origin: "o");

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = kept.Origin);
    }

    [TestMethod]
    public void Transact_NestedCall_JoinsTheOuterTransaction()
    {
        var doc = new YDoc();
        var afterTransactions = 0;
        doc.AfterTransaction += (_, _) => afterTransactions++;
        Transaction inner = null;
        Transaction outer = null;

        doc.Transact(tr =>
        {
            outer = tr;
            doc.Transact(t => inner = t, origin: "ignored");
            doc.GetText("t").Insert(0, "a");
        }, origin: "outer");

        Assert.AreEqual((1, true), (afterTransactions, ReferenceEquals(outer, inner)));
    }

    [TestMethod]
    public void TransactWithState_StaticLambda_RunsWithStateOriginAndLocal()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        (object Origin, bool Local) seen = default;
        doc.AfterTransaction += (_, tr) => seen = seen == default ? (tr.Origin, tr.Local) : seen;

        doc.Transact((text, value: "abc"), static (s, tr) => s.text.Insert(0, s.value), origin: "o", local: false);

        Assert.AreEqual(("abc", (object)"o", false), (text.ToString(), seen.Origin, seen.Local));
    }

    [TestMethod]
    public void Transact_NextTransaction_StartsWithNothingOfTheLastOne()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        text.Insert(0, "abc");
        doc.Transact(tr =>
        {
            tr.Meta["k"] = 1;
            text.Delete(0, 1);
        }, origin: "first");
        string seen = null;
        doc.AfterTransaction += (_, tr) => seen =
            $"{tr.Origin}|{string.Join(",", tr.Changed.Keys.Select(k => k == map ? "map" : "other"))}|{tr.Meta.Count}|" +
            $"{tr.DeleteSet.Clients.Count}|{tr.BeforeState[doc.ClientId]}|{tr.AfterState[doc.ClientId]}";

        map.Set("x", 1);

        Assert.AreEqual("|map|0|0|3|4", seen);
    }

    [TestMethod]
    public void Event_ChangesReadAfterTheHandler_Throws()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        YEvent kept = null;
        map.EventHandler += (_, e) => kept = e.Event;
        map.Set("a", 1);

        map.Set("b", 2);

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = kept.Changes);
    }

    [TestMethod]
    public void Event_ChangesReadInTheHandler_StayValidWhenTheTransactionIsReused()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        var keys = new List<IDictionary<string, ChangeKey>>();
        map.EventHandler += (_, e) => keys.Add(e.Event.Keys);
        map.Set("a", 1);

        map.Set("b", 2);

        Assert.AreEqual("a;b", string.Join(";", keys.Select(k => string.Join(",", k.Keys))));
    }

    [TestMethod]
    public void Cleanup_ObserverStartsATransaction_BothAreCleanedUpInOneBatch()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        text.EventHandler += (_, _) => map.Set("seen", true);
        var log = new List<string>();
        doc.AfterTransaction += (_, tr) => log.Add($"after:{tr.Origin}");
        doc.Update += (_, e) => log.Add($"update:{e.origin}");
        doc.AfterAllTransactions += (_, trs) => log.Add($"all:{trs.Count}:{trs[0] != trs[1]}");

        doc.Transact(_ => text.Insert(0, "a"), origin: "outer");

        Assert.AreEqual("after:outer,update:outer,after:,update:,all:2:True", string.Join(",", log));
    }

    [TestMethod]
    public void Cleanup_ObserverStartsTwoTransactions_EachIsItsOwn()
    {
        // As Yjs: a transaction an observer starts ends when its 'Transact' returns, so a second one
        // is a new transaction (it used to join the first).
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        text.EventHandler += (_, _) =>
        {
            map.Set("x", 1);
            map.Set("y", 2);
        };
        var updates = 0;
        doc.Update += (_, _) => updates++;

        text.Insert(0, "a");

        Assert.AreEqual(3, updates);
    }

    [TestMethod]
    public void Cleanup_AfterAllTransactionsStartsATransaction_OuterBatchListIsIntact()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var counts = new List<string>();
        var nested = false;
        doc.AfterAllTransactions += (_, trs) =>
        {
            if (!nested)
            {
                nested = true;
                var before = trs.Count;
                text.Insert(1, "b");
                counts.Add($"{before}->{trs.Count}");
            }
            else
            {
                counts.Add($"nested:{trs.Count}");
            }
        };

        text.Insert(0, "a");

        Assert.AreEqual("nested:1,1->1,ab", string.Join(",", counts) + "," + text);
    }

    [TestMethod]
    public void Transact_ActionThrows_TransactionIsCleanedUpAndTheNextOneIsFresh()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var updates = new List<string>();
        doc.Update += (_, e) => updates.Add((string)e.origin);
        string changed = null;
        doc.AfterTransaction += (_, tr) => changed = string.Join(",", tr.Changed.Keys.Select(k => k == text ? "text" : "map"));

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            throw new InvalidOperationException("action");
        }, origin: "failed"));
        doc.GetMap("m").Set("k", 1);

        Assert.AreEqual("failed,|map|a", string.Join(",", updates) + "|" + changed + "|" + text);
    }

    [TestMethod]
    public void Transact_ActionAndObserverThrow_TheObserversExceptionPropagates()
    {
        // As Yjs' transact: the cleanup runs in a 'finally', and its exception replaces the action's.
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.EventHandler += (_, _) => throw new ArgumentException("observer");

        var e = Assert.ThrowsExactly<ArgumentException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            throw new InvalidOperationException("action");
        }));

        Assert.AreEqual("observer", e.Message);
    }

    [TestMethod]
    public void Cleanup_ObserverThrows_OtherObserversAndAfterTransactionStillRun()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        var log = new List<string>();
        text.EventHandler += (_, _) => throw new InvalidOperationException("observer");
        map.EventHandler += (_, _) => log.Add("map");
        doc.AfterTransaction += (_, _) => log.Add("after");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            map.Set("k", 1);
        }));

        Assert.AreEqual("map,after", string.Join(",", log));
    }

    [TestMethod]
    public void Cleanup_ObserverThrows_NextTransactionWorksAndIsClean()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var fail = true;
        text.EventHandler += (_, _) =>
        {
            if (fail)
            {
                fail = false;
                throw new InvalidOperationException("observer");
            }
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => text.Insert(0, "a"));
        int changed = -1;
        doc.AfterTransaction += (_, tr) => changed = tr.Changed.Count;

        text.Insert(1, "b");

        Assert.AreEqual("ab|1|True", $"{text}|{changed}|{doc._transaction == null && doc._transactionCleanups.Count == 0}");
    }

    [TestMethod]
    public void Cleanup_DeepObservers_AreCalledBeforeAfterTransaction()
    {
        // Yjs' order: observers, deep observers, then 'afterTransaction'.
        var doc = new YDoc();
        var map = doc.GetMap("m");
        var log = new List<string>();
        map.EventHandler += (_, _) => log.Add("observe");
        map.DeepEventHandler += (_, _) => log.Add("deep");
        doc.AfterTransaction += (_, _) => log.Add("after");

        map.Set("k", 1);

        Assert.AreEqual("observe,deep,after", string.Join(",", log));
    }

    [TestMethod]
    public void Cleanup_DeepObserverOfNestedTypes_GetsTheEventsSortedByPath()
    {
        var doc = new YDoc();
        var root = doc.GetMap("m");
        var child = new YMap();
        root.Set("child", child);
        string paths = null;
        root.DeepEventHandler += (_, e) => paths = string.Join(",", e.Events.Select(evt => evt.Path.Count));

        doc.Transact(_ =>
        {
            child.Set("x", 1);
            root.Set("y", 2);
        });

        Assert.AreEqual("0,1", paths);
    }

    [TestMethod]
    public void Cleanup_NoSubdocumentChanged_RaisesNoSubdocsChanged()
    {
        var doc = new YDoc();
        var raised = 0;
        doc.SubdocsChanged += (_, _) => raised++;

        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual(0, raised);
    }

    [TestMethod]
    public void UndoManager_StackItemsAndEvents_StayValidAfterTheirTransactionsEnded()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var undo = new UndoManager(text);
        var added = new List<IDictionary<YType, IList<YEvent>>>();
        var popped = new List<IDictionary<YType, IList<YEvent>>>();
        undo.StackItemAdded += (_, e) => added.Add(e.ChangedParentTypes);
        undo.StackItemPopped += (_, e) => popped.Add(e.ChangedParentTypes);
        text.Insert(0, "a");
        undo.StopCapturing();
        text.Insert(1, "b");
        undo.StopCapturing();
        text.Delete(0, 1);

        undo.Undo();
        undo.Undo();
        var afterTwoUndos = text.ToString();
        undo.Redo();

        Assert.AreEqual(
            "a|ab|True|True",
            $"{afterTwoUndos}|{text}|{added.All(c => c.ContainsKey(text))}|{popped.Count == 3 && popped.All(c => c.ContainsKey(text))}");
    }

    [TestMethod]
    public void UndoManager_UndoInsideAnOuterTransaction_Undoes()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var undo = new UndoManager(text);
        text.Insert(0, "abc");
        var popped = 0;
        undo.StackItemPopped += (_, e) => popped += e.ChangedParentTypes == null ? 0 : 1;

        doc.Transact(_ => undo.Undo());

        Assert.AreEqual("|1", $"{text}|{popped}");
    }
}
