using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A staged struct enters the store only through <see cref="ItemRef.Adopt"/>, which reads the staging table and leaves it
/// as it was; a struct of the store is a slot, never an object.
/// </summary>
[TestClass]
public class ItemRefAdoptionTests
{
    [TestMethod]
    public void Library_EveryMethodBody_AdoptsStagedStructsOnlyInTheIntegrationPlansExecute()
    {
        var found = Callers(["ItemRef"], ["Adopt"]).Select(c => $"{c.Type}.{c.Method}").Distinct().ToArray();

        Assert.AreEqual("IntegrationPlan.Execute", string.Join("\n", found));
    }

    private static (YDoc Doc, StagingTable Table) Staged(int typeRef, string typeName)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 2 });
        doc.GetMap("m").TableIndex(doc.Store.Types);
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = table.Add(StagedKind.Type, 1);
        table.SetParentRoot(i, "m");
        table.SetParentSub(i, "k");
        table.SetType(i, typeRef, typeName);
        return (doc, table);
    }

    [TestMethod]
    [DataRow(0, null, typeof(YArray))]
    [DataRow(1, null, typeof(YMap))]
    [DataRow(2, null, typeof(YText))]
    [DataRow(3, "div", typeof(YXmlElement))]
    [DataRow(4, null, typeof(YXmlFragment))]
    [DataRow(5, "hook", typeof(YXmlHook))]
    [DataRow(6, null, typeof(YXmlText))]
    public void Adopt_StagedType_IsMadeThereWithItsNameAndItsTableIndex(int typeRef, string typeName, Type expected)
    {
        var (doc, table) = Staged(typeRef, typeName);
        var before = doc.Store.Types.Count;

        doc.Transact(tr => ItemRef.Adopt(table, 0, tr, 0, true), origin: null, local: false);

        var item = doc.Store.Find(new ItemId(1, 0));
        var type = item.TypeOf;
        Assert.AreEqual(
            (expected, typeName, before + 1, (long)type.TableIndexForTests),
            (type.GetType(), (type as YXmlElement)?.NodeName ?? (type as YXmlHook)?.HookName, doc.Store.Types.Count, doc.Store.Slab.Read(item.Handle).Payload));
    }

    [TestMethod]
    public void Adopt_StagedTypeNotIntegrated_MakesNoTypeAndNoSlot()
    {
        var (doc, table) = Staged(3, "div");
        var (types, live) = (doc.Store.Types.Count, doc.Store.Slab.LiveCount);

        doc.Transact(tr => ItemRef.Adopt(table, 0, tr, 0, false), origin: null, local: false);

        Assert.AreEqual((types, live), (doc.Store.Types.Count, doc.Store.Slab.LiveCount));
    }

    [TestMethod]
    public void Adopt_StagedColdContent_IsTheSlotsObject()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 2 });
        doc.GetArray("a");
        var binary = new ContentBinary([1, 2, 3]);
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = table.Add(StagedKind.Binary, 1);
        table.SetParentRoot(i, "a");
        table.SetCold(i, binary);

        doc.Transact(tr => ItemRef.Adopt(table, i, tr, 0, true), origin: null, local: false);

        Assert.AreSame(binary, doc.Store.Find(new ItemId(1, 0)).Cold);
    }

    [TestMethod]
    public void Adopt_StagedJsonAtAnOffset_LeavesTheTablesObjectAsItWas()
    {
        // A legacy ContentJSON item of two values in the root array 'a'.
        var update = Convert.FromBase64String("AQECAAIBAWECCXVuZGVmaW5lZAd7ImsiOjF9AA==");
        var table = new StagingTable();
        var decoder = new UpdateDecoderV1(update, long.MaxValue, UpdateLimits.DefaultMaxDepth);
        var cursor = EncodingUtils.StructCursor.Begin(ref decoder, update, table);
        cursor.NextStaged(ref decoder, update);
        var doc = new YDoc(new YDocOptions { ClientId = 2 });

        // The store holds the first clock of the struct's client, as when the first part came earlier.
        doc.GetArray("a").Add(1);
        var before = ((IContent)table.Cold(0)).Length;

        doc.Transact(tr => ItemRef.Adopt(table, 0, tr, 1, true), origin: null, local: false);

        Assert.AreEqual((2, 2, 1), (before, ((IContent)table.Cold(0)).Length, doc.Store.Find(new ItemId(2, 1)).Length));
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
