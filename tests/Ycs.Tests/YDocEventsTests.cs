using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YDocEventsTests
{
    [TestMethod]
    public void Remove_HandlerOfADocumentWithNoHandlers_DoesNothing()
    {
        var doc = new YDoc();

        doc.AfterTransaction -= (_, _) => { };
        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual("a", doc.GetText("t").ToString());
    }

    [TestMethod]
    public void AddAndRemove_TwoHandlers_OnlyTheOneLeftIsCalled()
    {
        var doc = new YDoc();
        var calls = new List<string>();
        EventHandler<Transaction> first = (_, _) => calls.Add("first");
        EventHandler<Transaction> second = (_, _) => calls.Add("second");
        doc.AfterTransaction += first;
        doc.AfterTransaction += second;
        doc.AfterTransaction -= first;

        doc.GetText("t").Insert(0, "a");

        Assert.AreEqual("second", string.Join(",", calls));
    }
}
