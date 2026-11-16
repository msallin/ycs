using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// No method of the library boxes an <see cref="ItemRef"/>: a box allocates, and a boxed reference compares and hashes
/// as an object, not as the struct it names (a reference turned into an object by mistake reads as never null).
/// </summary>
[TestClass]
public class ItemRefBoxingTests
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    [TestMethod]
    public void Library_EveryMethodBody_NeverBoxesAnItemRef()
    {
        using var pe = new PEReader(File.OpenRead(typeof(YDoc).Assembly.Location));
        var md = pe.GetMetadataReader();
        var itemRef = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == nameof(ItemRef));
        var found = new List<string>();
        var methods = 0;

        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            methods++;
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                var first = il.ReadByte();
                var op = OpCodesByValue[first == 0xFE ? (short)(0xFE00 | il.ReadByte()) : first];
                if (op == OpCodes.Box || op == OpCodes.Constrained)
                {
                    // The non-generic IEnumerator.Current of an IEnumerable<ItemRef>, which the library never calls, boxes by contract.
                    if (IsItemRef(MetadataTokens.EntityHandle(il.ReadInt32()), itemRef) && md.GetString(method.Name) != "System.Collections.IEnumerator.get_Current")
                    {
                        found.Add($"{md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name)}.{md.GetString(method.Name)}: {op.Name}");
                    }

                    continue;
                }

                Skip(ref il, op);
            }
        }

        Assert.IsTrue(methods > 1000);
        Assert.AreEqual(string.Empty, string.Join("\n", found));
    }

    // A constrained call on an ItemRef is fine for its own overrides (Equals, GetHashCode, ToString): only GetType boxes,
    // and the library never asks for it, so every constrained prefix on an ItemRef is reported.
    private static bool IsItemRef(EntityHandle token, TypeDefinitionHandle itemRef) =>
        token.Kind == HandleKind.TypeDefinition && (TypeDefinitionHandle)token == itemRef;

    private static void Skip(ref BlobReader il, OpCode op)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                il.Offset += 1;
                break;
            case OperandType.InlineVar:
                il.Offset += 2;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                il.Offset += 8;
                break;
            case OperandType.InlineSwitch:
                var targets = il.ReadInt32();
                il.Offset += 4 * targets;
                break;
            default:
                il.Offset += 4;
                break;
        }
    }
}
