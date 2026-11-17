using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The states a transaction reports are the document's state vector at its start and at the start of its cleanup, as copies would hold them.</summary>
[TestClass]
public class TransactionStateTests
{
    private static string Show(IEnumerable<KeyValuePair<long, long>> state) =>
        string.Join(",", state.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));

    private static YDoc DocWithClients(int clients)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.GetText("t").Insert(0, "base");
        for (var c = 0; c < clients; c++)
        {
            var peer = new YDoc(new YDocOptions { ClientId = 1000 + c });
            peer.ApplyUpdate(doc.EncodeStateAsUpdate());
            peer.GetText("t").Insert(0, "x");
            doc.ApplyUpdate(peer.EncodeStateAsUpdate(doc.EncodeStateVector()));
        }

        return doc;
    }

    [TestMethod]
    public void States_LocalEditOfADocumentWithManyClients_AreTheStateVectorsAroundIt()
    {
        var doc = DocWithClients(100);
        var text = doc.GetText("t");
        var expectedBefore = Show(doc.Store.GetStateVector());
        string? seen = null;
        doc.AfterTransaction += (_, tr) => seen = $"{Show(tr.BeforeState)}|{Show(tr.AfterState)}|{tr.BeforeState.Count}|{tr.AfterState.Count}";

        text.Insert(2, "yz");

        Assert.AreEqual($"{expectedBefore}|{Show(doc.Store.GetStateVector())}|101|101", seen);
    }

    [TestMethod]
    public void States_UpdateOfAnUnknownClient_LackOrHoldIt()
    {
        var doc = DocWithClients(70);
        var peer = new YDoc(new YDocOptions { ClientId = 9999 });
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        peer.GetText("t").Insert(0, "n");
        var update = peer.EncodeStateAsUpdate(doc.EncodeStateVector());
        string? seen = null;
        doc.AfterTransaction += (_, tr) =>
            seen = $"{tr.BeforeState.ContainsKey(9999)}|{tr.AfterState[9999]}|{tr.BeforeState.Count}|{tr.AfterState.Count}|{tr.BeforeState.TryGetValue(9999, out long _)}";

        doc.ApplyUpdate(update);

        Assert.AreEqual("False|1|71|72|False", seen);
    }

    [TestMethod]
    public void States_TransactionStartedByAnObserver_LeaveTheOnesBeforeItAsTheyWere()
    {
        var doc = DocWithClients(70);
        var first = doc.GetText("t");
        var other = doc.GetText("o");
        var expectedBefore = Show(doc.Store.GetStateVector());
        string? expectedAfter = null;
        var seen = new List<string>();
        doc.BeforeObserverCalls += (_, tr) =>
        {
            if (expectedAfter == null)
            {
                expectedAfter = Show(doc.Store.GetStateVector());
                other.Insert(0, "nested");
            }
        };
        doc.AfterTransaction += (_, tr) => seen.Add($"{Show(tr.BeforeState)}|{Show(tr.AfterState)}");

        first.Insert(0, "a");

        Assert.AreEqual($"{expectedBefore}|{expectedAfter}", seen[0]);
        Assert.AreEqual($"{expectedAfter}|{Show(doc.Store.GetStateVector())}", seen[1]);
    }

    [TestMethod]
    public void States_EndedTransactionKept_AreEmpty()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        IReadOnlyDictionary<long, long>? before = null;
        doc.AfterTransaction += (_, tr) => before = tr.BeforeState;
        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual(0, before!.Count);
    }

    [TestMethod]
    public void States_EnumeratedWhileATransactionAddsAClient_KeepTheClientsOfTheirPoint()
    {
        var doc = DocWithClients(3);
        var peer = new YDoc(new YDocOptions { ClientId = 9999 });
        peer.GetText("t").Insert(0, "n");
        var update = peer.EncodeStateAsUpdate();
        string? seen = null;
        var applied = false;
        doc.BeforeObserverCalls += (_, tr) =>
        {
            if (seen != null)
            {
                return;
            }

            var keys = new List<long>();
            foreach (var (client, _) in tr.AfterState)
            {
                keys.Add(client);
                if (!applied)
                {
                    applied = true;
                    doc.ApplyUpdate(update);
                }
            }

            seen = string.Join(",", keys.OrderBy(k => k));
        };

        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual("1,1000,1001,1002", seen);
    }

    [TestMethod]
    public void States_AfterAllTransactionsHandlerStartingBatches_LeaveTheOuterTransactionsStates()
    {
        var doc = DocWithClients(2);
        var expectedBefore = Show(doc.Store.GetStateVector());
        string? expectedAfter = null;
        string? seen = null;
        var batches = 0;
        doc.BeforeObserverCalls += (_, _) => expectedAfter ??= Show(doc.Store.GetStateVector());
        doc.AfterAllTransactions += (_, transactions) =>
        {
            if (batches++ > 0)
            {
                return;
            }

            doc.GetText("o").Insert(0, "inner");
            doc.GetText("o").Insert(0, "inner");
            seen = $"{Show(transactions[0].BeforeState)}|{Show(transactions[0].AfterState)}";
        };

        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual(($"{expectedBefore}|{expectedAfter}", 0), (seen, doc.Store.ChangeCount));
    }

    [TestMethod]
    public void States_HandlersThatThrow_EmptyTheLogAndKeepTheNextStates()
    {
        var doc = DocWithClients(2);
        var text = doc.GetText("t");
        var throwing = true;
        doc.AfterTransaction += (_, _) =>
        {
            if (throwing)
            {
                throw new System.InvalidOperationException("handler");
            }
        };
        Assert.ThrowsExactly<System.InvalidOperationException>(() => text.Insert(0, "a"));
        var afterThrow = doc.Store.ChangeCount;
        throwing = false;
        var expectedBefore = Show(doc.Store.GetStateVector());
        string? seen = null;
        doc.AfterTransaction += (_, tr) => seen = $"{Show(tr.BeforeState)}|{Show(tr.AfterState)}";

        text.Insert(0, "b");

        Assert.AreEqual((0, $"{expectedBefore}|{Show(doc.Store.GetStateVector())}"), (afterThrow, seen));
    }

    [TestMethod]
    public void Log_BatchOfManyClients_GivesBackItsCapacity()
    {
        var source = DocWithClients(1500);
        var doc = new YDoc(new YDocOptions { ClientId = 5 });

        doc.ApplyUpdate(source.EncodeStateAsUpdate());

        Assert.AreEqual((0, 4), (doc.Store.ChangeCount, doc.Store.ChangeCapacity));
    }
}
