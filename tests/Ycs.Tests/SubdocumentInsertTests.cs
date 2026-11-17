using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class SubdocumentInsertTests
{
    [TestMethod]
    public void Add_DocumentThatIsASubdocumentAlready_ThrowsAndInsertsNothing()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        var sub = new YDoc();
        array.Add([sub]);

        Assert.ThrowsExactly<ArgumentException>(() => array.Add([sub]));
        Assert.ThrowsExactly<ArgumentException>(() => doc.GetMap("m")["k"] = sub);
        Assert.AreEqual(1, array.Length);
        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void Add_SameDocumentTwiceInOneBatch_ThrowsAndInsertsNothing()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        var sub = new YDoc();

        Assert.ThrowsExactly<ArgumentException>(() => array.Add([new YDoc(), sub, sub]));
        Assert.AreEqual(0, array.Length);
    }

    [TestMethod]
    public void Add_DocumentIntoItselfOrIntoItsOwnSubdocument_Throws()
    {
        var root = new YDoc();
        var child = new YDoc();
        root.GetArray("a").Add([child]);

        Assert.ThrowsExactly<ArgumentException>(() => root.GetArray("a").Add([root]));
        Assert.ThrowsExactly<ArgumentException>(() => child.GetArray("a").Add([root]));
        Assert.AreEqual(0, child.GetArray("a").Length);
    }

    [TestMethod]
    public void Add_DocumentAfterDestroy_MakesANewSubdocument()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        var sub = new YDoc();
        array.Add([sub]);
        sub.Destroy();

        array.Add([sub]);

        Assert.AreEqual(2, array.Length);
    }
}
