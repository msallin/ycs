using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary><see cref="Transaction.SubdocsAdded"/>, <see cref="Transaction.SubdocsLoaded"/> and <see cref="Transaction.SubdocsRemoved"/> read inside a transaction.</summary>
[TestClass]
public class TransactionSubdocsTests
{
    private static string Guids(IReadOnlySet<YDoc> docs) => string.Join(",", docs.Select(d => d.Guid).Order(StringComparer.Ordinal));

    private static string Sets(Transaction tr) => $"{Guids(tr.SubdocsAdded)}|{Guids(tr.SubdocsRemoved)}|{Guids(tr.SubdocsLoaded)}";

    [TestMethod]
    public void SubdocsAdded_SubdocumentSetIntoAMap_HoldsIt()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        string seen = null;

        doc.Transact(tr =>
        {
            map["s"] = new YDoc(new YDocOptions { Guid = "sub" });
            seen = Guids(tr.SubdocsAdded);
        });

        Assert.AreEqual("sub", seen);
    }

    [TestMethod]
    public void SubdocsLoaded_NewSubdocumentThatShouldLoad_HoldsItToo()
    {
        var doc = new YDoc();
        string seen = null;

        doc.Transact(tr =>
        {
            doc.GetMap("m")["s"] = new YDoc(new YDocOptions { Guid = "sub" });
            seen = Sets(tr);
        });

        Assert.AreEqual("sub||sub", seen);
    }

    [TestMethod]
    public void SubdocsRemoved_SubdocumentDeletedFromAMap_HoldsIt()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["s"] = new YDoc(new YDocOptions { Guid = "sub" });
        string seen = null;

        doc.Transact(tr =>
        {
            map.Remove("s");
            seen = Sets(tr);
        });

        Assert.AreEqual("|sub|", seen);
    }

    [TestMethod]
    public void SubdocsLoaded_LoadOfANotLoadedSubdocument_HoldsOnlyIt()
    {
        var doc = new YDoc();
        var subdoc = new YDoc(new YDocOptions { Guid = "sub", ShouldLoad = false });
        doc.GetMap("m")["s"] = subdoc;
        string seen = null;

        doc.Transact(tr =>
        {
            subdoc.Load();
            seen = Sets(tr);
        });

        Assert.AreEqual("||sub", seen);
    }

    [TestMethod]
    public void Sets_TransactionThatTouchesNoSubdocument_AreEmpty()
    {
        var doc = new YDoc();
        string seen = null;

        doc.Transact(tr =>
        {
            doc.GetText("t").Insert(0, "x");
            seen = Sets(tr);
        });

        Assert.AreEqual("||", seen);
    }

    [TestMethod]
    public void Sets_AfterTheTransactionEnded_ThrowInvalidOperation()
    {
        var doc = new YDoc();
        Transaction ended = null;
        doc.Transact(tr => ended = tr);

        var thrown = new List<Type>();
        foreach (var read in new Func<Transaction, object>[] { t => t.SubdocsAdded, t => t.SubdocsLoaded, t => t.SubdocsRemoved })
        {
            try
            {
                read(ended);
            }
            catch (InvalidOperationException e)
            {
                thrown.Add(e.GetType());
            }
        }

        CollectionAssert.AreEqual(new[] { typeof(InvalidOperationException), typeof(InvalidOperationException), typeof(InvalidOperationException) }, thrown);
    }
}
