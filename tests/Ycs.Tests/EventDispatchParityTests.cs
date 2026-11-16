using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Observer dispatch in the transaction cleanup, as Yjs 13.6.32 does it (results pinned from Yjs).</summary>
[TestClass]
public class EventDispatchParityTests
{
    private static string PathOf(YEvent e) => "[" + string.Join(",", e.Path) + "]";

    [TestMethod]
    public void ObserveDeep_OnAMiddleType_CurrentTargetIsTheObservedType()
    {
        var doc = new YDoc();
        var r = doc.GetMap("r");
        var mid = new YMap();
        r.Set("mid", mid);
        var leaf = new YArray();
        mid.Set("leaf", leaf);
        var log = new List<string>();
        mid.ObserveDeep((deepEvents, _) => log.AddRange(deepEvents.Select(ev => (ev.CurrentTarget == mid ? "mid" : "other") + PathOf(ev))));

        leaf.Add(new object[] { 1 });

        CollectionAssert.AreEqual(new[] { "mid[leaf]" }, log);
    }

    [TestMethod]
    public void AfterTransaction_NoDeepObserver_CurrentTargetIsTheTarget()
    {
        var doc = new YDoc();
        var r = doc.GetMap("r");
        var mid = new YMap();
        r.Set("mid", mid);
        var leaf = new YArray();
        mid.Set("leaf", leaf);
        var log = new List<string>();
        doc.AfterTransaction += (_, tr) =>
        {
            if (tr.ChangedParentTypes.TryGetValue(leaf, out var events))
            {
                log.AddRange(events.Select(ev => (ev.CurrentTarget == leaf ? "leaf" : "other") + PathOf(ev)));
            }
        };

        leaf.Add(new object[] { 1 });

        CollectionAssert.AreEqual(new[] { "leaf[]" }, log);
    }

    [TestMethod]
    public void ObserveDeep_RootAndNestedMapChanged_PathIsRelativeToTheRoot()
    {
        var doc = new YDoc();
        var root = doc.GetMap("root");
        var child = new YMap();
        root.Set("c", child);
        var log = new List<string>();
        root.ObserveDeep((deepEvents, _) => log.AddRange(deepEvents.Select(ev => (ev.Target == root ? "root" : "child") + (ev.CurrentTarget == root ? " root" : " other") + PathOf(ev))));

        doc.Transact(_ =>
        {
            root.Set("x", 1);
            child.Set("y", 2);
        });

        CollectionAssert.AreEqual(new[] { "root root[]", "child root[c]" }, log);
    }

    [TestMethod]
    public void Observe_ChangedTypeDeletedInTheSameTransaction_OthersKeepTheOrderOfChange()
    {
        var doc = new YDoc();
        var a = doc.GetArray("A");
        var p = doc.GetMap("P");
        var c = doc.GetArray("C");
        var b = new YArray();
        p.Set("b", b);
        var log = new List<string>();
        a.Observe(_ => log.Add("A"));
        p.Observe(_ => log.Add("P"));
        c.Observe(_ => log.Add("C"));
        b.Observe(_ => log.Add("B"));

        doc.Transact(_ =>
        {
            a.Add(new object[] { 1 });
            b.Add(new object[] { 1 });
            p.Delete("b");
            c.Add(new object[] { 1 });
        });

        CollectionAssert.AreEqual(new[] { "A", "P", "C" }, log);
    }

    [TestMethod]
    public void Observe_FirstObserverThrows_TheSecondIsCalledAndTheExceptionPropagates()
    {
        var doc = new YDoc();
        var a = doc.GetArray("a");
        var log = new List<string>();
        a.Observe(_ =>
        {
            log.Add("1");
            throw new InvalidOperationException("first");
        });
        a.Observe(_ => log.Add("2"));

        Assert.ThrowsExactly<InvalidOperationException>(() => a.Add(new object[] { 1 }));

        CollectionAssert.AreEqual(new[] { "1", "2" }, log);
    }

    [TestMethod]
    public void ObserveDeep_FirstDeepObserverThrows_TheSecondIsCalled()
    {
        var doc = new YDoc();
        var a = doc.GetArray("a");
        var log = new List<string>();
        a.ObserveDeep((_, _) =>
        {
            log.Add("1");
            throw new InvalidOperationException("first");
        });
        a.ObserveDeep((_, _) => log.Add("2"));

        Assert.ThrowsExactly<InvalidOperationException>(() => a.Add(new object[] { 1 }));

        CollectionAssert.AreEqual(new[] { "1", "2" }, log);
    }

    [TestMethod]
    public void ObserveDeep_TargetDeletedByAnEarlierObserver_EventIsNotDelivered()
    {
        var doc = new YDoc();
        var root = doc.GetMap("root");
        var x = new YArray();
        var y = new YArray();
        root.Set("x", x);
        root.Set("y", y);
        x.Observe(_ => root.Delete("y"));
        var log = new List<string>();
        root.ObserveDeep((deepEvents, _) => log.Add(string.Join(",", deepEvents.Select(ev => ev.Target == x ? "x" : ev.Target == y ? "y" : "other"))));

        doc.Transact(_ =>
        {
            y.Add(new object[] { 1 });
            x.Add(new object[] { 1 });
        });

        CollectionAssert.AreEqual(new[] { "x", "other" }, log);
    }

    [TestMethod]
    public void ObserveDeep_AttachedByAnObserverDuringTheCleanup_GetsTheEventsOfEarlierTypes()
    {
        var doc = new YDoc();
        var root = doc.GetMap("root");
        var x = new YArray();
        var y = new YArray();
        root.Set("x", x);
        root.Set("y", y);
        var log = new List<string>();
        var attached = false;
        y.Observe(_ =>
        {
            if (!attached)
            {
                attached = true;
                root.ObserveDeep((deepEvents, _) => log.Add(string.Join(",", deepEvents.Select(ev => ev.Target == x ? "x" : ev.Target == y ? "y" : "other"))));
            }
        });

        doc.Transact(_ =>
        {
            x.Add(new object[] { 1 });
            y.Add(new object[] { 1 });
        });

        CollectionAssert.AreEqual(new[] { "x,y" }, log);
    }
}
