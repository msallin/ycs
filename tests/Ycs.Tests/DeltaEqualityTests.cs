using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class DeltaEqualityTests
{
    [TestMethod]
    public void ToDelta_ApplyDeltaThenToDelta_IsEqual()
    {
        var doc = new YDoc();
        var source = doc.GetText("a");
        source.Insert(0, "Hello world");
        source.Format(0, 5, [new("bold", true), new("size", 3)]);
        var target = doc.GetText("b");

        target.ApplyDelta(source.ToDelta());

        Assert.IsTrue(source.ToDelta().SequenceEqual(target.ToDelta()));
    }

    [TestMethod]
    public void Equals_SameEntriesInAnotherOrder_IsEqualWithTheSameHash()
    {
        YAttributes a = [new("bold", true), new("size", 3)];
        YAttributes b = [new("size", 3), new("bold", true)];

        Assert.AreEqual((true, true, true), (a.Equals(b), a.GetHashCode() == b.GetHashCode(), a.Equals((object)b)));
    }

    [TestMethod]
    public void Equals_DifferentKeysValuesOrCounts_IsNotEqual()
    {
        YAttributes a = [new("bold", true), new("size", 3)];

        Assert.AreEqual(
            (false, false, false, false, false),
            (a.Equals([new("bold", true), new("size", 4)]), a.Equals([new("bold", true), new("other", 3)]), a.Equals([new("bold", true)]), a.Equals((YAttributes?)null), a.Equals("x")));
    }

    [TestMethod]
    public void ToString_Attributes_IsJson()
    {
        YAttributes a = [new("bold", true), new("size", 3)];

        Assert.AreEqual(("{\"bold\":true,\"size\":3}", "{}"), (a.ToString(), YAttributes.Empty.ToString()));
    }

    [TestMethod]
    public void Equals_InsertWithEqualAttributes_IsEqual()
    {
        var a = new Delta.Insert("x") { Attributes = [new("bold", true)] };
        var b = new Delta.Insert("x") { Attributes = [new("bold", true)] };

        Assert.AreEqual((true, a.GetHashCode() == b.GetHashCode()), (a == b, true));
    }

    [TestMethod]
    public void ToString_InsertWithAttributes_PrintsTheAttributes() =>
        Assert.AreEqual("Insert { Attributes = {\"bold\":true}, Value = \"x\" }", new Delta.Insert("x") { Attributes = [new("bold", true)] }.ToString());

    [TestMethod]
    public void InsertItems_FromAnArrayEvent_ComparesAndPrintsItsValues()
    {
        var items = new List<IReadOnlyList<Delta>>();
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Observe(e => items.Add(e.Delta));

        array.Insert(0, 1, "two");
        array.Insert(2, 1, "two");

        var first = items[0].OfType<Delta.InsertItems>().Single();
        var second = items[1].OfType<Delta.InsertItems>().Single();
        Assert.AreEqual(
            (true, true, "InsertItems { Attributes = , Items = [1, \"two\"] }"),
            (first == second, first.GetHashCode() == second.GetHashCode(), first.ToString()));
    }

    [TestMethod]
    public void InsertItems_DifferentItems_AreNotEqual() =>
        Assert.AreEqual(
            (false, false),
            (new Delta.InsertItems([1]) == new Delta.InsertItems([2]), new Delta.InsertItems([1]).Equals(null)));

    [TestMethod]
    public void ToString_UpdateMeta_ListsTheRangesByClient()
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, "abc");

        var meta = YUpdate.ParseMeta(doc.EncodeStateAsUpdate());

        Assert.AreEqual($"UpdateMeta {{ Ranges = [{doc.ClientId}: 0..3] }}", meta.ToString());
    }

    [TestMethod]
    public void ToString_UpdateMetaWithoutAMatchingTo_PrintsZero() =>
        Assert.AreEqual("UpdateMeta { Ranges = [1: 4..0] }", new UpdateMeta(new Dictionary<long, long> { [1] = 4 }, new Dictionary<long, long>()).ToString());
}
