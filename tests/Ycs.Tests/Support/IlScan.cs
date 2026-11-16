using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Ycs;

/// <summary>Reads the IL of the library's assembly, for the tests that forbid a call or an instruction anywhere but in one type.</summary>
internal static class IlScan
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>One instruction that carries a metadata token (a call, a box, a constrained prefix, ...).</summary>
    internal readonly record struct TokenOp(OpCode Op, EntityHandle Token);

    /// <summary>
    /// The instructions with a token of every method body of the library, with the method's outermost declaring type and name.
    /// The metadata reader is valid until the result is disposed.
    /// </summary>
    internal sealed class Result(
        PEReader pe,
        int methods,
        List<(string Type, string Method, TypeDefinitionHandle Outer, TypeDefinitionHandle Declaring, TokenOp Op)> ops,
        MetadataReader metadata) : IDisposable
    {
        public void Deconstruct(out int methodCount, out List<(string Type, string Method, TypeDefinitionHandle Outer, TypeDefinitionHandle Declaring, TokenOp Op)> operations, out MetadataReader reader)
        {
            methodCount = methods;
            operations = ops;
            reader = metadata;
        }

        public void Dispose() => pe.Dispose();
    }

    internal static Result Read()
    {
        var pe = new PEReader(File.OpenRead(typeof(YDoc).Assembly.Location));
        var md = pe.GetMetadataReader();
        var ops = new List<(string, string, TypeDefinitionHandle, TypeDefinitionHandle, TokenOp)>();
        var methods = 0;

        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            methods++;
            var declaring = method.GetDeclaringType();
            var outer = declaring;
            while (md.GetTypeDefinition(outer).GetDeclaringType() is { IsNil: false } parent)
            {
                outer = parent;
            }

            var typeName = md.GetString(md.GetTypeDefinition(declaring).Name);
            var methodName = md.GetString(method.Name);
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (il.RemainingBytes > 0)
            {
                var first = il.ReadByte();
                var op = OpCodesByValue[first == 0xFE ? (short)(0xFE00 | il.ReadByte()) : first];
                if (op.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineField)
                {
                    ops.Add((typeName, methodName, outer, declaring, new TokenOp(op, MetadataTokens.EntityHandle(il.ReadInt32()))));
                    continue;
                }

                Skip(ref il, op);
            }
        }

        return new Result(pe, methods, ops, md);
    }

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
