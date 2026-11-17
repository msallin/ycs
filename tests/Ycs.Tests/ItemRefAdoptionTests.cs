using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A decoded struct enters the store only through <see cref="ItemRef.Adopt"/>, and the library allocates an
/// <see cref="Item"/> or <see cref="GC"/> object only for the decoded form: a struct of the store is a slot, never an object.
/// </summary>
[TestClass]
public class ItemRefAdoptionTests
{
    [TestMethod]
    public void Library_EveryMethodBody_AllocatesItemAndGcObjectsOnlyWhereAnUpdateIsDecoded()
    {
        var found = Callers(["Item", "GC"], [".ctor"], OpCodes.Newobj).Select(c => $"{c.Outer}.{c.Method}").Distinct().OrderBy(s => s, StringComparer.Ordinal);

        Assert.AreEqual("EncodingUtils.ReadStruct", string.Join("\n", found));
    }

    [TestMethod]
    public void Library_EveryMethodBody_AdoptsDecodedStructsOnlyInTheIntegrationPlansExecute()
    {
        var found = Callers(["ItemRef"], ["Adopt"]).Select(c => $"{c.Type}.{c.Method}").Distinct().ToArray();

        Assert.AreEqual("IntegrationPlan.Execute", string.Join("\n", found));
    }

    private static List<(string Outer, string Type, string Method, string Target)> Callers(string[] types, string[] names, OpCode? only = null)
    {
        using var scan = IlScan.Read();
        var (_, ops, md) = scan;
        var wanted = md.TypeDefinitions.Where(h => types.Contains(md.GetString(md.GetTypeDefinition(h).Name))).ToHashSet();
        var callers = new List<(string, string, string, string)>();
        foreach (var (type, method, outer, _, op) in ops)
        {
            if (op.Op != OpCodes.Call && op.Op != OpCodes.Callvirt && op.Op != OpCodes.Ldftn && op.Op != OpCodes.Ldvirtftn && op.Op != OpCodes.Ldtoken && op.Op != OpCodes.Newobj)
            {
                continue;
            }

            if (only != null && op.Op != only.Value)
            {
                continue;
            }

            var target = Target(md, op.Token);
            if (wanted.Contains(target.Declaring) && names.Contains(target.Name))
            {
                callers.Add((md.GetString(md.GetTypeDefinition(outer).Name), type, method, target.Name));
            }
        }

        return callers;
    }

    // Within one assembly a call names a method definition (or, generic, a specification of one).
    private static (TypeDefinitionHandle Declaring, string Name) Target(MetadataReader md, EntityHandle token)
    {
        if (token.Kind == HandleKind.MethodSpecification)
        {
            token = md.GetMethodSpecification((MethodSpecificationHandle)token).Method;
        }

        return token.Kind == HandleKind.MethodDefinition
            ? (md.GetMethodDefinition((MethodDefinitionHandle)token).GetDeclaringType(), md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)token).Name))
            : default;
    }
}
