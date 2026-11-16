using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A decoded struct enters the store only through <see cref="ItemRef.Adopt"/>, and no method of the library but
/// <see cref="ItemRef"/>'s own wraps an object in a reference or takes the object out of one
/// (<c>Of</c>, <c>ToStruct</c>, <c>ToItem</c>): the object disappears behind the reference in step 7.
/// </summary>
[TestClass]
public class ItemRefAdoptionTests
{
    [TestMethod]
    public void Library_EveryMethodBody_CallsNoObjectAccessOfItemRefOutsideItemRef()
    {
        var found = Callers(["Of", "ToStruct", "ToItem"]).Where(c => c.Outer != "ItemRef").Select(c => $"{c.Type}.{c.Method} calls {c.Target}");

        Assert.AreEqual(string.Empty, string.Join("\n", found.Distinct().OrderBy(s => s, StringComparer.Ordinal)));
    }

    [TestMethod]
    public void Library_EveryMethodBody_AdoptsDecodedStructsOnlyInTheIntegrationPlansExecute()
    {
        var found = Callers(["Adopt"]).Select(c => $"{c.Type}.{c.Method}").Distinct().ToArray();

        Assert.AreEqual("IntegrationPlan.Execute", string.Join("\n", found));
    }

    [TestMethod]
    public void Library_EveryMethodBody_WrapsADecodedStructInAReferenceOnlyForItsViews()
    {
        var found = Callers(["Decoded"]).Select(c => $"{c.Type}.{c.Method}").Distinct().ToArray();

        Assert.AreEqual("ItemView.Detached", string.Join("\n", found));
    }

    private static List<(string Outer, string Type, string Method, string Target)> Callers(string[] names)
    {
        var (_, ops, md) = IlScan.Read();
        var itemRef = md.TypeDefinitions.Single(h => md.GetString(md.GetTypeDefinition(h).Name) == nameof(ItemRef));
        var callers = new List<(string, string, string, string)>();
        foreach (var (type, method, outer, _, op) in ops)
        {
            if (op.Op != OpCodes.Call && op.Op != OpCodes.Callvirt && op.Op != OpCodes.Ldftn && op.Op != OpCodes.Newobj)
            {
                continue;
            }

            var target = Target(md, op.Token);
            if (target.Declaring == itemRef && names.Contains(target.Name))
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
