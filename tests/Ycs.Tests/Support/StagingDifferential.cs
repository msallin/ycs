#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Reads an update three ways, into objects, into objects shadowed by a <see cref="StagingTable"/> and straight into a
/// table, and requires the same structs, the same exception at the same struct, sound tables, and every staged struct
/// equal to the one detached from the objects. Linked into Ycs.Fuzz.
/// </summary>
internal static class StagingDifferential
{
    /// <summary>The number of structs read; fails the test on any difference.</summary>
    public static int Check(byte[] input, bool v2, long maxStructs)
    {
        var plain = Read(input, v2, maxStructs, null, out var plainError);
        var table = new StagingTable();
        var staged = Read(input, v2, maxStructs, table, out var stagedError);

        Assert.AreEqual(plainError, stagedError, "the same exception, or none");
        var count = plain.Values.Sum(l => l.Count);
        Assert.AreEqual(count, staged.Values.Sum(l => l.Count), "the same structs");
        Assert.AreEqual(count, table.Count, "one staged struct per object");
        AssertShadows(staged, table);

        var direct = new StagingTable();
        var directError = ReadDirect(input, v2, maxStructs, direct);
        Assert.AreEqual(plainError, directError, "the same exception from the direct read, or none");
        Assert.AreEqual(count, direct.Count, "one directly staged struct per object");
        AssertShadows(staged, direct);

        return count;
    }

    /// <summary>Every struct of <paramref name="table"/> equals the one detached from <paramref name="objects"/>, and the table is sound.</summary>
    public static void AssertShadows(Dictionary<long, List<AbstractStruct>> objects, StagingTable table)
    {
        var violations = table.Violations();
        Assert.IsEmpty(violations, string.Join("; ", violations));
        foreach (var (client, list) in objects)
        {
            var (start, n) = table.RangeOf(table.FindClient(client));
            Assert.AreEqual(list.Count, n, $"client {client}");
            var detached = Detach(list);
            for (var k = 0; k < n; k++)
            {
                AssertEqual(detached[k], StructStaging.Decoded(table, start + k));
            }
        }
    }

    /// <summary>The records the struct objects give: the oracle of the staged ones.</summary>
    public static DecodedStruct[] Detach(IReadOnlyList<AbstractStruct> structs)
    {
        var result = new DecodedStruct[structs.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Detach(structs[i]);
        }

        return result;
    }

    private static DecodedStruct Detach(AbstractStruct s)
    {
        var none = Array.Empty<object>();
        if (s is not Item item)
        {
            return new DecodedStruct { Id = s.Id, Length = s.Length, Kind = s is Skip ? ItemKind.Skip : ItemKind.GC };
        }

        var content = item.Content;
        IReadOnlyList<object?> values = none;
        string? text = null, formatKey = null, typeName = null, docGuid = null;
        object? formatValue = null;
        var typeRef = -1;
        switch (content)
        {
            case ContentJson or ContentBinary or ContentEmbed or ContentAny:
                values = content.GetContent();
                break;
            case ContentString str:
                text = str.GetString();
                break;
            case ContentFormat format:
                formatKey = format.Key;
                formatValue = format.Value;
                break;
            case ContentType type:
                (typeRef, typeName) = (type.TypeRef, type.TypeName);
                break;
            case ContentDoc doc:
                docGuid = doc.Doc.Guid;
                break;
        }

        return new DecodedStruct
        {
            Id = item.Id,
            Length = item.Length,
            Kind = (ItemKind)content.Ref,
            LeftOrigin = item.LeftOrigin,
            RightOrigin = item.RightOrigin,
            ParentName = item.Parent as string,
            ParentId = item.Parent as ItemId?,
            ParentSub = item.ParentSub,
            Content = values,
            Text = text,
            FormatKey = formatKey,
            FormatValue = formatValue,
            TypeRef = typeRef,
            TypeName = typeName,
            DocGuid = docGuid,
        };
    }

    public static void AssertEqual(DecodedStruct expected, DecodedStruct actual)
    {
        Assert.AreEqual(
            (expected.Id, expected.Length, expected.Kind, expected.LeftOrigin, expected.RightOrigin, expected.ParentName, expected.ParentId, expected.ParentSub),
            (actual.Id, actual.Length, actual.Kind, actual.LeftOrigin, actual.RightOrigin, actual.ParentName, actual.ParentId, actual.ParentSub));
        Assert.AreEqual(
            (expected.Text, expected.FormatKey, expected.TypeRef, expected.TypeName, expected.DocGuid),
            (actual.Text, actual.FormatKey, actual.TypeRef, actual.TypeName, actual.DocGuid));
        Assert.AreEqual(YValue.FromStored(expected.FormatValue), YValue.FromStored(actual.FormatValue), $"the format value of {expected.Id}");
        Assert.IsTrue(expected.Content.Select(YValue.FromStored).SequenceEqual(actual.Content.Select(YValue.FromStored)), $"the values of {expected.Id}");
    }

    // Two reads of one update give equal values, not the same objects.
    internal static bool DeepEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (IDictionary<string, object?> x, IDictionary<string, object?> y) =>
            x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var other) && DeepEqual(p.Value, other)),
        (System.Collections.IList x, System.Collections.IList y) =>
            x.Count == y.Count && Enumerable.Range(0, x.Count).All(i => DeepEqual(x[i], y[i])),
        _ => Equals(a, b),
    };

    private static string? ReadDirect(byte[] input, bool v2, long maxStructs, StagingTable table)
    {
        try
        {
            var reader = new Lib0Reader(input);
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader, maxStructs) : new UpdateDecoderV1(reader, maxStructs);
            var cursor = new EncodingUtils.StructCursor(decoder, [], table);
            while (cursor.NextStaged() >= 0)
            {
            }

            return null;
        }
        catch (Exception e)
        {
            return $"{e.GetType().FullName}: {e.Message}";
        }
    }

    private static Dictionary<long, List<AbstractStruct>> Read(byte[] input, bool v2, long maxStructs, StagingTable? staging, out string? error)
    {
        var refs = new Dictionary<long, List<AbstractStruct>>();
        error = null;
        try
        {
            var reader = new Lib0Reader(input);
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader, maxStructs) : new UpdateDecoderV1(reader, maxStructs);
            EncodingUtils.ReadClientStructRefs(decoder, refs, [], null, staging);
        }
        catch (Exception e)
        {
            error = $"{e.GetType().FullName}: {e.Message}";
        }

        return refs;
    }
}
