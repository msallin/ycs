using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The library holds the structs of the store through <see cref="ItemRef"/>: no internal signature (field, property,
/// parameter, return) names <see cref="Item"/> or <see cref="AbstractStruct"/> but the struct classes themselves, the
/// public members, and the decoded form, which stays objects until the decoder writes staging tables
/// (docs/design-item-layout.md, 2.5). The decoded form's members are listed here: a new one fails the test.
/// </summary>
[TestClass]
public class ItemRefSignatureTests
{
    // The decoded form (an update's structs as read, waiting, merged or planned), the members that carry a public
    // filter (Predicate<ItemView>, Func<ItemView, bool>) to its call, and the hooks of the tests.
    private static readonly string[] Decoded =
    [
        "YType.TypeMapEnumerate",
        "YType.ItemForTests",
        "YType.StartForTests",
        "YType.get_ItemForTests",
        "YType.get_StartForTests",
        "YType.MapForTests",
        "Lease.*",
        "StructCursor.Next",
        "StructStaging.Stage",
        "WriteState.Struct",
        "YDocOptions.DefaultGcFilter",
        "Decoded..ctor",
        "DeleteSet.TryGc",
        "DeleteSet.TryGcDeleteSet",
        "EncodingUtils.ReadClientStructRefs",
        "EncodingUtils.ReadClientStructRefsInto",
        "EncodingUtils.ReadStruct",
        "IntegrationPlan.*",
        "ItemView.*",
        "KeyChains.*",
        "LazyStructReader.*",
        "LazyStructWriter.WriteStruct",
        "ClientStructRefs.*",
        "PendingStructs.*",
        "PlanItem.*",
        "StructStore.ApplyUpdate",
        "StructStore.CheckPendingLimits",
        "StructStore.WriteRest",
        "StructStore.NoRefs",
        "StructStore.StructListsForTests",
        "UpdateOperations*",
        "UpdateReader*",
        "DecodedUpdate*",
        "YDoc.DefaultGcFilter",
        "UndoManager._deleteFilter",
    ];

    [TestMethod]
    public void Library_InternalSignatures_NameNoStructObjectOutsideTheDecodedForm()
    {
        var structs = new HashSet<Type> { typeof(Item), typeof(AbstractStruct), typeof(GC), typeof(Skip), typeof(ItemRef) };
        var found = new List<string>();
        foreach (var type in typeof(YDoc).Assembly.GetTypes())
        {
            if (structs.Contains(type) || type.IsDefined(typeof(CompilerGeneratedAttribute)) || (type.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute)) ?? false))
            {
                continue;
            }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var member in type.GetMembers(all))
            {
                if (IsVisibleOutside(member) || member.IsDefined(typeof(CompilerGeneratedAttribute)) || member.Name.Contains('<'))
                {
                    continue;
                }

                if (Signature(member).Any(t => Names(t, structs)) && !IsDecoded(type, member))
                {
                    found.Add($"{type.Name}.{member.Name}");
                }
            }
        }

        Assert.AreEqual(string.Empty, string.Join("\n", found.Distinct().OrderBy(s => s, StringComparer.Ordinal)));
    }

    private static bool IsDecoded(Type type, MemberInfo member) =>
        Decoded.Any(entry => entry.EndsWith('*')
            ? $"{type.Name}.{member.Name}".StartsWith(entry.TrimEnd('*'), StringComparison.Ordinal)
            : entry == $"{type.Name}.{member.Name}");

    private static bool IsVisibleOutside(MemberInfo member)
    {
        bool Visible(MethodBase m) => m != null && (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) && IsTypeVisible(m.DeclaringType);
        return member switch
        {
            FieldInfo f => (f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly) && IsTypeVisible(f.DeclaringType),
            MethodBase m => Visible(m),
            PropertyInfo p => Visible(p.GetMethod) || Visible(p.SetMethod),
            EventInfo e => Visible(e.AddMethod),
            _ => true,
        };
    }

    private static bool IsTypeVisible(Type type) => type.IsPublic || (type.IsNestedPublic && IsTypeVisible(type.DeclaringType!));

    private static IEnumerable<Type> Signature(MemberInfo member) => member switch
    {
        FieldInfo f => [f.FieldType],
        PropertyInfo p => [p.PropertyType],
        MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
        ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
        _ => [],
    };

    private static bool Names(Type type, HashSet<Type> structs)
    {
        if (type.HasElementType)
        {
            return Names(type.GetElementType()!, structs);
        }

        return (structs.Contains(type) && type != typeof(ItemRef)) || (type.IsGenericType && type.GetGenericArguments().Any(a => Names(a, structs)));
    }
}
