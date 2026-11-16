using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A document whose BeforeAllTransactions, BeforeTransaction, AfterTransactionCleanup, Update, UpdateV2 or
/// SubdocsChanged handler throws stays usable: a deliberate difference from Yjs 13.6, whose document is broken
/// afterwards (yjs#799; docs/api.md). The exception still escapes; the cleanup is finished first.
/// </summary>
[TestClass]
public class HandlerRecoveryTests
{
    public static IEnumerable<object[]> Events =>
        new[] { "BeforeAllTransactions", "BeforeTransaction", "AfterTransactionCleanup", "Update", "UpdateV2", "SubdocsChanged" }
            .Select(e => new object[] { e });

    private static bool IsBefore(string evt) => evt is "BeforeAllTransactions" or "BeforeTransaction";

    /// <summary>Adds a handler of <paramref name="evt"/> that throws once, with the event's name as the message.</summary>
    private static void ThrowOnce(YDoc doc, string evt)
    {
        var armed = true;
        void Throw()
        {
            if (armed)
            {
                armed = false;
                throw new InvalidOperationException(evt);
            }
        }

        switch (evt)
        {
            case "BeforeAllTransactions": doc.BeforeAllTransactions += (_, _) => Throw(); break;
            case "BeforeTransaction": doc.BeforeTransaction += (_, _) => Throw(); break;
            case "AfterTransactionCleanup": doc.AfterTransactionCleanup += (_, _) => Throw(); break;
            case "Update": doc.Updated += (_, _) => Throw(); break;
            case "UpdateV2": doc.UpdatedV2 += (_, _) => Throw(); break;
            case "SubdocsChanged": doc.SubdocsChanged += (_, _) => Throw(); break;
            default: throw new ArgumentException(evt);
        }
    }

    // The first edit adds a subdocument (so that SubdocsChanged fires), the others type and delete.
    private static void Edit(YDoc doc, int i)
    {
        var text = doc.GetText("t");
        switch (i)
        {
            case 0:
                doc.Transact(_ =>
                {
                    text.Insert(0, "a");
                    doc.GetMap("m").Set("sub", new YDoc(new YDocOptions { Guid = "s" }));
                });
                break;
            case 4:
                text.Delete(1, 2);
                break;
            default:
                text.Insert(text.Length, ((char)('a' + i)).ToString());
                break;
        }
    }

    private static bool QueueEmpty(YDoc doc) => doc._transaction == null && doc._transactionCleanups.Count == 0;

    [TestMethod]
    [DynamicData(nameof(Events))]
    public void Handler_ThrowsOnce_DocumentRecoversAndTheExceptionEscapes(string evt)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        ThrowOnce(doc, evt);
        var updates = new List<byte[]>();
        doc.Updated += (_, e) => updates.Add(e.Update.ToArray());
        var observerCalls = 0;
        doc.GetText("t").Observe(_ => observerCalls++);
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        var reference = new YDoc(new YDocOptions { ClientId = 1 });

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => Edit(doc, 0)).Message;
        updates.Clear();
        observerCalls = 0;
        for (var i = 1; i <= 4; i++)
        {
            Edit(doc, i);
        }

        // A peer that got only the updates after the throw converges once it resyncs by state vector.
        foreach (var update in updates)
        {
            peer.ApplyUpdate(update);
        }

        peer.ApplyUpdate(doc.EncodeStateAsUpdate(peer.EncodeStateVector()));

        // The same edits without a throwing handler: the same structs, collected and merged alike.
        for (var i = IsBefore(evt) ? 1 : 0; i <= 4; i++)
        {
            Edit(reference, i);
        }

        var actual = $"escaped: {escaped}, observer calls: {observerCalls}, updates: {updates.Count}, " +
            $"peer: {peer.GetText("t")}, converged: {peer.EncodeStateVector().AsSpan().SequenceEqual(doc.EncodeStateVector())}, " +
            $"as without the throw: {doc.EncodeStateAsUpdate().AsSpan().SequenceEqual(reference.EncodeStateAsUpdate())}, " +
            $"subdocuments: {doc.Subdocs.Count}, queue empty: {QueueEmpty(doc)}";
        var text = IsBefore(evt) ? "b" : "ad";
        var subdocs = IsBefore(evt) ? 0 : 1;
        Assert.AreEqual(
            $"escaped: {evt}, observer calls: 4, updates: 4, peer: {text}, converged: True, as without the throw: True, " +
            $"subdocuments: {subdocs}, queue empty: True",
            actual);
    }

    [TestMethod]
    public void BeforeTransaction_Throws_TheFunctionDoesNotRunAndTheEmptyTransactionIsCleanedUp()
    {
        var doc = new YDoc();
        ThrowOnce(doc, "BeforeTransaction");
        var log = new List<string>();
        doc.AfterTransaction += (_, tr) => log.Add($"afterTransaction {tr.Origin}");
        doc.Updated += (_, _) => log.Add("update");
        doc.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");
        var ran = false;

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            ran = true;
            doc.GetText("t").Insert(0, "a");
        }, origin: "o"));

        Assert.AreEqual("ran: False, text: , queue empty: True, log: afterTransaction o afterAllTransactions",
            $"ran: {ran}, text: {doc.GetText("t")}, queue empty: {QueueEmpty(doc)}, log: {string.Join(" ", log)}");
    }

    [TestMethod]
    public void BeforeAllTransactions_Throws_BeforeTransactionIsSkippedAsYjsEmitsInOrder()
    {
        var doc = new YDoc();
        ThrowOnce(doc, "BeforeAllTransactions");
        var beforeTransaction = 0;
        doc.BeforeTransaction += (_, _) => beforeTransaction++;

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetText("t").Insert(0, "a"));
        doc.GetText("t").Insert(0, "b");

        Assert.AreEqual("before transaction: 1, text: b", $"before transaction: {beforeTransaction}, text: {doc.GetText("t")}");
    }

    [TestMethod]
    public void Update_ThrowsInANestedTransaction_EscapesFromTheOutermostTransactAfterItsFunction()
    {
        var doc = new YDoc();
        ThrowOnce(doc, "Update");
        var log = new List<string>();
        doc.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");
        var text = doc.GetText("t");

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            doc.Transact(_ => text.Insert(0, "a"));
            log.Add("inner returned");
            doc.Transact(_ => text.Insert(1, "b"));
            log.Add("outer function done");
        })).Message;
        text.Insert(2, "c");

        Assert.AreEqual(
            "escaped: Update, log: inner returned outer function done afterAllTransactions afterAllTransactions, text: abc, queue empty: True",
            $"escaped: {escaped}, log: {string.Join(" ", log)}, text: {text}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    [DataRow(0, DisplayName = "the first transaction's update throws")]
    [DataRow(1, DisplayName = "the queued transaction's update throws")]
    public void Update_ThrowsWhileATransactionAnObserverStartedIsQueued_BothAreCleanedUp(int throwingUpdate)
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        var seen = 0;
        doc.Updated += (_, _) =>
        {
            if (seen++ == throwingUpdate)
            {
                throw new InvalidOperationException($"update {throwingUpdate}");
            }
        };
        var delivered = 0;
        doc.Updated += (_, _) => delivered++;
        var mapObserved = 0;
        map.Observe(_ => mapObserved++);
        text.Observe(_ =>
        {
            if (!map.ContainsKey("k"))
            {
                map.Set("k", 1);
            }
        });
        var afterAll = 0;
        doc.AfterAllTransactions += (_, _) => afterAll++;

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => text.Insert(0, "a")).Message;
        text.Insert(1, "b");

        Assert.AreEqual(
            $"escaped: update {throwingUpdate}, delivered: 2, map observed: 1, after all: 2, queue empty: True",
            $"escaped: {escaped}, delivered: {delivered}, map observed: {mapObserved}, after all: {afterAll}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    public void Handlers_SeveralThrowInOneCleanup_EveryStepRunsAndTheLastExceptionEscapes()
    {
        var doc = new YDoc();
        var log = new List<string>();
        void Throw(string what)
        {
            log.Add(what);
            throw new InvalidOperationException(what);
        }

        doc.GetText("t").Observe(_ => Throw("observer"));
        doc.AfterTransactionCleanup += (_, _) => Throw("afterTransactionCleanup");
        doc.Updated += (_, _) => log.Add("update");
        doc.UpdatedV2 += (_, _) => Throw("updateV2");
        doc.SubdocsChanged += (_, _) => Throw("subdocs");
        doc.AfterAllTransactions += (_, _) => log.Add("afterAllTransactions");

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            doc.GetText("t").Insert(0, "a");
            doc.GetMap("m").Set("sub", new YDoc());
        })).Message;

        Assert.AreEqual(
            "escaped: subdocs, log: observer afterTransactionCleanup update updateV2 subdocs afterAllTransactions, queue empty: True",
            $"escaped: {escaped}, log: {string.Join(" ", log)}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    public void AfterAllTransactions_ThrowsAfterAnUpdateHandlerThrew_ItsExceptionEscapesAsTheLast()
    {
        var doc = new YDoc();
        doc.Updated += (_, _) => throw new InvalidOperationException("update");
        doc.AfterAllTransactions += (_, _) => throw new InvalidOperationException("afterAllTransactions");

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetText("t").Insert(0, "a")).Message;

        Assert.AreEqual("afterAllTransactions", escaped);
    }

    [TestMethod]
    public void Update_FirstOfTwoHandlersThrows_TheSecondMissesThatUpdateOnly()
    {
        var doc = new YDoc();
        ThrowOnce(doc, "Update");
        var second = 0;
        doc.Updated += (_, _) => second++;

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetText("t").Insert(0, "a"));
        doc.GetText("t").Insert(1, "b");

        Assert.AreEqual(1, second);
    }

    [TestMethod]
    public void SubdocsChanged_ThrowsForARemovedSubdocument_ItIsStillRemovedAndDestroyed()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        var sub = new YDoc(new YDocOptions { Guid = "s" });
        map.Set("sub", sub);
        var destroyed = false;
        sub.Destroyed += (_, _) => destroyed = true;
        ThrowOnce(doc, "SubdocsChanged");

        Assert.ThrowsExactly<InvalidOperationException>(() => map.Delete("sub"));

        Assert.AreEqual("destroyed: True, subdocuments: 0, queue empty: True",
            $"destroyed: {destroyed}, subdocuments: {doc.Subdocs.Count}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    [DataRow("AfterTransactionCleanup")]
    [DataRow("Update")]
    [DataRow("UpdateV2")]
    public void ApplyUpdate_HandlerThrowsForARemoteUpdate_TheUpdateIsAppliedAndTheExceptionEscapes(string evt)
    {
        var remote = new YDoc(new YDocOptions { ClientId = 2 });
        var remoteUpdates = new List<byte[]>();
        remote.Updated += (_, e) => remoteUpdates.Add(e.Update.ToArray());
        remote.GetText("t").Insert(0, "a");
        remote.GetText("t").Insert(1, "b");
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        ThrowOnce(doc, evt);
        var announced = 0;
        doc.Updated += (_, _) => announced++;

        var escaped = Assert.ThrowsExactly<InvalidOperationException>(() => doc.ApplyUpdate(remoteUpdates[0])).Message;
        doc.ApplyUpdate(remoteUpdates[1]);

        Assert.AreEqual($"escaped: {evt}, text: ab, announced: {(evt == "Update" ? 1 : 2)}, queue empty: True",
            $"escaped: {escaped}, text: {doc.GetText("t")}, announced: {announced}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    public void ApplyUpdate_BeforeTransactionThrows_TheUpdateIsNotAppliedAndCanBeAppliedAgain()
    {
        var remote = new YDoc(new YDocOptions { ClientId = 2 });
        remote.GetText("t").Insert(0, "a");
        var update = remote.EncodeStateAsUpdate();
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        ThrowOnce(doc, "BeforeTransaction");

        var e = Assert.ThrowsExactly<InvalidOperationException>(() => doc.ApplyUpdate(update));
        var after = doc.GetText("t").ToString();
        doc.ApplyUpdate(update);

        Assert.AreEqual("escaped: BeforeTransaction, after the throw: , then: a, queue empty: True",
            $"escaped: {e.Message}, after the throw: {after}, then: {doc.GetText("t")}, queue empty: {QueueEmpty(doc)}");
    }

    [TestMethod]
    [DataRow("BeforeTransaction")]
    [DataRow("AfterTransactionCleanup")]
    [DataRow("Update")]
    public void UndoManager_HandlerThrowsOnce_UndoAndRedoStillWorkAndAreAnnounced(string evt)
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var undo = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
        ThrowOnce(doc, evt);
        var announced = 0;
        doc.Updated += (_, _) => announced++;

        Assert.ThrowsExactly<InvalidOperationException>(() => text.Insert(0, "a"));
        text.Insert(text.Length, "b");
        var stack = undo.UndoStack.Count;
        announced = 0;
        undo.Undo();
        var undone = text.ToString();
        undo.Undo();
        var undoneTwice = text.ToString();
        undo.Redo();

        var expected = evt == "BeforeTransaction" ? "stack: 1, undone: , twice: , redone: b, announced: 2" : "stack: 2, undone: a, twice: , redone: a, announced: 3";
        Assert.AreEqual($"{expected}, queue empty: True",
            $"stack: {stack}, undone: {undone}, twice: {undoneTwice}, redone: {text}, announced: {announced}, queue empty: {QueueEmpty(doc)}");
    }
}
