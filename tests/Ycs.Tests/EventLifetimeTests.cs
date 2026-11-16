using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Events are built only when observed, and their transaction is reused once it has ended: every
/// event handed to user code lets go of its transaction when the transaction ends, whichever way
/// it reached the user (a handler, a deep handler, an undo manager's stack event, an observer of a
/// transaction that an observer started). What an event reads from its transaction is computed in
/// the handler or throws afterwards; it never reads the transaction that reuses the object.
/// </summary>
[TestClass]
public class EventLifetimeTests
{
    [TestMethod]
    public void Event_KeptAfterTheHandler_ReadsOfTheTransactionThrow()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        YTextEvent kept = null;
        text.Observe(e => kept ??= (YTextEvent)e);
        text.Insert(0, "a");
        text.Insert(1, "b");

        Assert.AreEqual(
            "True|True|True|True|True",
            $"{ThrowsEnded(() => kept.Delta)}|{ThrowsEnded(() => kept.Changes)}|{ThrowsEnded(() => kept.Keys)}|" +
            $"{ThrowsEnded(() => kept.Transaction.Origin)}|{ThrowsEnded(() => kept.Adds(text._start))}");
    }

    [TestMethod]
    public void Event_DeltaReadAfterTheHandler_StartsNoTransaction()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        YEvent kept = null;
        text.Observe(e => kept = e);
        text.Insert(0, "a");
        var transactions = 0;
        doc.BeforeTransaction += (_, _) => transactions++;

        ThrowsEnded(() => kept.Delta);

        Assert.AreEqual(0, transactions);
    }

    [TestMethod]
    public void Event_ComputedInTheHandler_KeepsItsOwnChangesAfterTheTransactionIsReused()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var kept = new List<YTextEvent>();
        text.Observe(e =>
        {
            _ = e.Delta;
            kept.Add((YTextEvent)e);
        });
        text.Insert(0, "ab");
        text.Format(0, 1, new Dictionary<string, object> { ["bold"] = true });

        Assert.AreEqual("ab|1:bold", $"{kept[0].Delta[0].Insert}|{kept[1].Delta[0].Retain}:{string.Join(",", kept[1].Delta[0].Attributes.Keys)}");
    }

    [TestMethod]
    public void Event_KeysChangedReadAfterTheHandler_AreTheKeysOfItsOwnTransaction()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var kept = new List<YTextEvent>();
        text.Observe(e => kept.Add((YTextEvent)e));
        text.SetAttribute("lang", "en");
        doc.Transact(_ =>
        {
            text.Insert(0, "a");
            text.SetAttribute("dir", "ltr");
        });

        Assert.AreEqual(
            "lang:False|dir:True",
            $"{string.Join(",", kept[0].KeysChanged)}:{kept[0].ChildListChanged}|{string.Join(",", kept[1].KeysChanged)}:{kept[1].ChildListChanged}");
    }

    [TestMethod]
    public void Event_OfADeepObserverOnly_IsDetachedWhenItsTransactionEnds()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        var inner = new YArray();
        map.Set("a", inner);
        IReadOnlyList<YEvent> kept = null;
        map.ObserveDeep((deepEvents, _) => kept = deepEvents);
        inner.Add(new object[] { 1 });
        doc.GetText("other").Insert(0, "x");

        Assert.AreEqual("True|True", $"{kept[0].Target == inner}|{ThrowsEnded(() => kept[0].Changes)}");
    }

    [TestMethod]
    public void Event_InAStackItemEventOfAnUnobservedType_IsDetachedWhenItsTransactionEnds()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var undo = new UndoManager(text);
        IDictionary<YType, IList<YEvent>> kept = null;
        undo.StackItemAdded += (_, e) => kept = e.ChangedParentTypes;
        text.Insert(0, "a");
        undo.Undo();

        Assert.AreEqual("True|True", $"{kept[text][0].Target == text}|{ThrowsEnded(() => kept[text][0].Delta)}");
    }

    [TestMethod]
    public void Event_OfATransactionAnObserverStarted_IsValidInItsHandlerAndDetachedAfterTheBatch()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        YEvent textEvent = null;
        YEvent mapEvent = null;
        string outerDeltaInTheNestedHandler = null;
        text.Observe(e =>
        {
            textEvent = e;
            doc.Transact(_ => map.Set("k", 1));
        });
        map.Observe(e =>
        {
            mapEvent = e;
            outerDeltaInTheNestedHandler = (string)textEvent.Delta[0].Insert;
            _ = e.Keys;
        });

        text.Insert(0, "a");
        doc.GetText("other").Insert(0, "x");

        Assert.AreEqual(
            "a|Add|True|True",
            $"{outerDeltaInTheNestedHandler}|{mapEvent.Keys["k"].Action}|{ThrowsEnded(() => mapEvent.Changes)}|{ThrowsEnded(() => textEvent.Transaction.Doc)}");
    }

    [TestMethod]
    public void Event_KeptWhileTheDocumentRunsOtherTransactions_NeverSeesTheirData()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var kept = new List<YEvent>();
        text.Observe(e => kept.Add(e));
        text.Insert(0, "a");
        var first = kept[0];
        Transaction reusing = null;
        doc.Transact(tr =>
        {
            reusing = tr;
            text.Insert(1, "b");
        });

        Assert.AreEqual("True|True", $"{!ReferenceEquals(first.Transaction, reusing)}|{ThrowsEnded(() => first.Transaction.BeforeState)}");
    }

    private static bool ThrowsEnded(Func<object> read)
    {
        try
        {
            read();
            return false;
        }
        catch (InvalidOperationException e)
        {
            return e.Message.StartsWith("This transaction has ended", StringComparison.Ordinal);
        }
    }
}
