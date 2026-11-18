using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public sealed class YTypeRevisionTests
{
    [TestMethod]
    public void Revision_OneTransactionWithManyEdits_GrowsByOne()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var before = text.Revision;

        doc.Transact(_ =>
        {
            text.Insert(0, "abc");
            text.Remove(1, 1);
            text.Format(0, 1, new System.Collections.Generic.Dictionary<string, YValue> { ["bold"] = true });
        });

        Assert.AreEqual(before + 1, text.Revision);
    }

    [TestMethod]
    public void Revision_RemoteUpdate_Grows()
    {
        var local = new YDoc();
        var remote = new YDoc();
        remote.GetText("t").Insert(0, "hello");
        var text = local.GetText("t");
        var before = text.Revision;

        local.ApplyUpdate(remote.EncodeStateAsUpdate());

        Assert.AreEqual(before + 1, text.Revision);
    }

    [TestMethod]
    public void Revision_OtherTypeChanged_Unchanged()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "x");
        var before = text.Revision;

        doc.GetMap("m")["k"] = 1;

        Assert.AreEqual(before, text.Revision);
    }

    [TestMethod]
    public void Revision_Read_AllocatesNothing()
    {
        var text = new YDoc().GetText("t");
        text.Insert(0, "x");
        _ = text.Revision;

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0L;
        for (var i = 0; i < 1000; i++)
        {
            sum += text.Revision;
        }

        Assert.AreEqual((0L, 1000L), (GC.GetAllocatedBytesForCurrentThread() - allocated, sum));
    }
}
