using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Outside the item layout and the decoded form, no method reads <see cref="ItemRef.Content"/> or type-tests a content
/// class: the callers go through the accessors of <see cref="ItemRef"/> (<c>Kind</c>, <c>Chars</c>, <c>Cold</c>, ...), so
/// that the storage under them can move. Each package that moves its callers deletes its own group below.
/// </summary>
[TestClass]
public class ContentAccessArchitectureTests
{
    // Types by name prefix. The item layout and the decoded form stay for good.
    private static readonly string[] Layout =
    [
        "ItemRef", "Content", "Item", "GC", "Skip", "UpdateReader", "EncodingUtils", "StructStaging", "PlanItem",
        "UpdateOperations", "LazyStructWriter",
    ];

    // Package 8.2b: array, map and xml callers.
    private static readonly string[] ArrayAndMapCallers = ["YArrayBase", "YArray", "YMap", "YType", "YXmlFragment", "YXmlElement", "YXmlNodeHelpers"];

    // Package 8.2c: the other callers. StructStore is one type over its partial files (IntegrationPlan, Checks, TestDump
    // are the ones that read content); the IL does not tell the files apart.
    private static readonly string[] OtherCallers =
    [
        "YEvent", "UndoManager", "PermanentUserData", "AbsolutePosition", "YDoc", "ItemView", "StructStore",
    ];

    [TestMethod]
    public void Library_EveryMethodBody_ReadsContentOnlyInTheAllowedTypes()
    {
        using var scan = IlScan.Read();
        var (methods, ops, md) = scan;
        var allowed = Layout.Concat(ArrayAndMapCallers).Concat(OtherCallers).ToArray();
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (type, method, outer, _, op) in ops)
        {
            var outerName = md.GetString(md.GetTypeDefinition(outer).Name);
            if (allowed.Any(prefix => outerName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            if (ReadsContent(md, op) is { } what)
            {
                found.Add($"{outerName} ({type}.{method}): {what}");
            }
        }

        Assert.IsTrue(methods > 1000);
        Assert.AreEqual(string.Empty, string.Join("\n", found));
    }

    private static string? ReadsContent(MetadataReader md, IlScan.TokenOp op)
    {
        if (op.Op == OpCodes.Isinst || op.Op == OpCodes.Castclass)
        {
            if (op.Token.Kind == HandleKind.TypeDefinition
                && md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)op.Token).Name).StartsWith("Content", StringComparison.Ordinal))
            {
                return $"{op.Op.Name} {md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)op.Token).Name)}";
            }

            return null;
        }

        if ((op.Op == OpCodes.Call || op.Op == OpCodes.Callvirt) && op.Token.Kind == HandleKind.MethodDefinition)
        {
            var callee = md.GetMethodDefinition((MethodDefinitionHandle)op.Token);
            if (md.GetString(callee.Name) == "get_Content" && md.GetString(md.GetTypeDefinition(callee.GetDeclaringType()).Name) == nameof(ItemRef))
            {
                return "ItemRef.Content";
            }
        }

        return null;
    }
}
