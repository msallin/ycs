using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// No method of the library boxes an <see cref="ItemRef"/>: a box allocates, and a boxed reference compares and hashes
/// as an object, not as the struct it names (a reference turned into an object by mistake reads as never null).
/// </summary>
[TestClass]
public class ItemRefBoxingTests
{
    [TestMethod]
    public void Library_EveryMethodBody_NeverBoxesAnItemRef()
    {
        var (methods, ops, md) = IlScan.Read();
        var itemRef = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == nameof(ItemRef));
        var found = new List<string>();

        foreach (var (type, method, _, _, op) in ops)
        {
            // The non-generic IEnumerator.Current of an IEnumerable<ItemRef>, which the library never calls, boxes by contract.
            if ((op.Op == OpCodes.Box || op.Op == OpCodes.Constrained) && IsItemRef(op.Token, itemRef) && method != "System.Collections.IEnumerator.get_Current")
            {
                found.Add($"{type}.{method}: {op.Op.Name}");
            }
        }

        Assert.IsTrue(methods > 1000);
        Assert.AreEqual(string.Empty, string.Join("\n", found));
    }

    // A constrained call on an ItemRef is fine for its own overrides (Equals, GetHashCode, ToString): only GetType boxes,
    // and the library never asks for it, so every constrained prefix on an ItemRef is reported.
    private static bool IsItemRef(EntityHandle token, TypeDefinitionHandle itemRef) =>
        token.Kind == HandleKind.TypeDefinition && (TypeDefinitionHandle)token == itemRef;
}
