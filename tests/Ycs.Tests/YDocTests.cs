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

[TestClass]
public class YDocTests : YTestBase
{
    [TestMethod]
    public void GenerateNewClientId_ManyDraws_Uses32BitsAsYjs()
    {
        // 64 draws all below 2^31 happen with probability 2^-64.
        var ids = Enumerable.Range(0, 64).Select(_ => YDoc.GenerateNewClientId()).ToList();

        Assert.IsTrue(ids.All(id => id is >= 0 and <= uint.MaxValue) && ids.Any(id => id > int.MaxValue));
    }

    [TestMethod]
    public void TestClientIdDuplicateChange()
    {
        var doc1 = new YDoc();
        doc1.ClientId = 0;
        var doc2 = new YDoc();
        doc2.ClientId = 0;
        Assert.AreEqual(doc1.ClientId, doc2.ClientId);

        doc1.GetArray("a").Insert(0, 1, 2);
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);
        Assert.AreNotEqual(doc1.ClientId, doc2.ClientId);
    }

    [TestMethod]
    public void TestGetTypeEmptyId()
    {
        var doc1 = new YDoc();
        doc1.GetText(string.Empty).Insert(0, "h");
        doc1.GetText().Insert(1, "i");

        var doc2 = new YDoc();
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);

        Assert.AreEqual("hi", doc2.GetText().ToString());
        Assert.AreEqual("hi", doc2.GetText(string.Empty).ToString());
    }

    [TestMethod]
    public void TestSubdoc()
    {
        var doc = new YDoc();
        doc.Load();

        {
            List<List<string>> events = null;
            doc.SubdocsChanged += (s, e) =>
            {
                events = new List<List<string>>();
                events.Add(new List<string>(e.Added.Select(d => d.Guid)));
                events.Add(new List<string>(e.Removed.Select(d => d.Guid)));
                events.Add(new List<string>(e.Loaded.Select(d => d.Guid)));
            };

            var subdocs = doc.GetMap("mysubdocs");
            var docA = new YDoc(new YDocOptions { Guid = "a" });
            docA.Load();
            subdocs["a"] = docA;
            CollectionAssert.AreEqual(new[] { "a" }, events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(new[] { "a" }, events[2]);
            events = null;

            (subdocs["a"].ToObject() as YDoc).Load();
            Assert.IsNull(events);
            events = null;

            (subdocs["a"].ToObject() as YDoc).Destroy();
            CollectionAssert.AreEqual(new[] { "a" }, events[0]);
            CollectionAssert.AreEqual(new[] { "a" }, events[1]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[2]);
            events = null;

            (subdocs["a"].ToObject() as YDoc).Load();
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(new[] { "a" }, events[2]);
            events = null;

            subdocs["b"] = new YDoc(new YDocOptions { Guid = "a", ShouldLoad = false });
            CollectionAssert.AreEqual(new[] { "a" }, events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[2]);
            events = null;

            (subdocs["b"].ToObject() as YDoc).Load();
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(new[] { "a" }, events[2]);
            events = null;

            var docC = new YDoc(new YDocOptions { Guid = "c" });
            docC.Load();
            subdocs["c"] = docC;
            CollectionAssert.AreEqual(new[] { "c" }, events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(new[] { "c" }, events[2]);
            events = null;

            var guids = doc.GetSubdocGuids().ToList();
            guids.Sort();
            CollectionAssert.AreEqual(new[] { "a", "c" }, guids);
        }

        var doc2 = new YDoc();

        {
            Assert.AreEqual(0, doc2.GetSubdocGuids().Count());

            List<List<string>> events = null;
            doc2.SubdocsChanged += (s, e) =>
            {
                events = new List<List<string>>();
                events.Add(new List<string>(e.Added.Select(d => d.Guid)));
                events.Add(new List<string>(e.Removed.Select(d => d.Guid)));
                events.Add(new List<string>(e.Loaded.Select(d => d.Guid)));
            };

            doc2.ApplyUpdate(doc.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);
            CollectionAssert.AreEqual(new[] { "a", "a", "c" }, events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[2]);
            events = null;

            (doc2.GetMap("mysubdocs")["a"].ToObject() as YDoc).Load();
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[0]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[1]);
            CollectionAssert.AreEqual(new[] { "a" }, events[2]);
            events = null;

            var guids = doc2.GetSubdocGuids().ToList();
            guids.Sort();
            CollectionAssert.AreEqual(new[] { "a", "c" }, guids);

            doc2.GetMap("mysubdocs").Remove("a");
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[0]);
            CollectionAssert.AreEqual(new[] { "a" }, events[1]);
            CollectionAssert.AreEqual(System.Array.Empty<object>(), events[2]);
            events = null;

            guids = doc2.GetSubdocGuids().ToList();
            guids.Sort();
            CollectionAssert.AreEqual(new[] { "a", "c" }, guids);
        }
    }

    [TestMethod]
    public void Get_RootOfAnotherType_NamesBothAndTheGetter()
    {
        var doc = new YDoc();
        doc.GetText("content");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetMap("content"));

        Assert.AreEqual("The root 'content' is a YText; it cannot be read as a YMap. Use GetText(\"content\").", ex.Message);
    }
}
