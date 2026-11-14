// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class DocApiTests
{
    [TestMethod]
    public void UnsuffixedMethodsUseTheV1Format()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 7 });
        doc.GetText("text").Insert(0, "hello");

        CollectionAssert.AreEqual(doc.EncodeStateAsUpdateV1(), doc.EncodeStateAsUpdate());
        CollectionAssert.AreEqual(doc.EncodeStateVectorV1(), doc.EncodeStateVector());

        var copy = new YDoc();
        copy.ApplyUpdate(doc.EncodeStateAsUpdate().AsSpan());
        Assert.AreEqual("hello", copy.GetText("text").ToString());
    }

    [TestMethod]
    public void EncodeIntoBufferWriterMatchesByteArray()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 7 });
        doc.GetText("text").Insert(0, "hello");
        doc.GetMap("map").Set("k", 1.5);

        var v1 = new ArrayBufferWriter<byte>();
        doc.EncodeStateAsUpdate(v1);
        CollectionAssert.AreEqual(doc.EncodeStateAsUpdateV1(), v1.WrittenSpan.ToArray());

        var v2 = new ArrayBufferWriter<byte>();
        doc.EncodeStateAsUpdateV2(v2);
        CollectionAssert.AreEqual(doc.EncodeStateAsUpdateV2(), v2.WrittenSpan.ToArray());

        // Diffed against a remote state vector: nothing is missing.
        var remote = new YDoc();
        remote.ApplyUpdate(v1.WrittenSpan);
        var diff = doc.EncodeStateAsUpdate(remote.EncodeStateVector());
        var empty = new YDoc();
        empty.ApplyUpdate(diff);
        Assert.AreEqual(string.Empty, empty.GetText("text").ToString());
    }

    [TestMethod]
    public void ClientIdCanBeChosenButNotNegative()
    {
        Assert.AreEqual(42, new YDoc(new YDocOptions { ClientId = 42 }).ClientId);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new YDocOptions { ClientId = -1 });
    }

    [TestMethod]
    public void DisposingTheObserveResultUnsubscribes()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var calls = 0;
        var subscription = text.Observe((_, _) => calls++);

        text.Insert(0, "a");
        subscription.Dispose();
        text.Insert(0, "b");

        Assert.AreEqual(1, calls);
    }
}
