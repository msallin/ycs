using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class KindEnumTests
{
    private static Dictionary<string, int> Values<T>(params T[] except) where T : struct, Enum =>
        Enum.GetValues<T>().Except(except).ToDictionary(v => v.ToString(), v => Convert.ToInt32(v));

    [TestMethod]
    public void ItemKind_SlotKindAndStagedKind_HaveTheSameNamesAndValues()
    {
        var item = Values<ItemKind>();

        // SlotKind adds Free, the marker of an unused slot, which no struct has.
        CollectionAssert.AreEquivalent(item, Values<SlotKind>(SlotKind.Free));
        CollectionAssert.AreEquivalent(item, Values<StagedKind>());
    }
}
