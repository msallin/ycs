#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Reads an update twice, with and without a <see cref="StagingTable"/>, and requires the same structs, the same
/// exception at the same struct, a sound table, and every staged struct equal to the one detached from the objects.
/// Linked into Ycs.Fuzz.
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
        var violations = table.Violations();
        Assert.IsEmpty(violations, string.Join("; ", violations));

        foreach (var (client, list) in staged)
        {
            var (start, n) = table.RangeOf(table.FindClient(client));
            Assert.AreEqual(list.Count, n, $"client {client}");
            var detached = UpdateOperations.Detach(list);
            for (var k = 0; k < n; k++)
            {
                AssertEqual(detached[k], StructStaging.Decoded(table, start + k));
            }
        }

        return count;
    }

    public static void AssertEqual(DecodedStruct expected, DecodedStruct actual)
    {
        Assert.AreEqual(
            (expected.Id, expected.Length, expected.Kind, expected.LeftOrigin, expected.RightOrigin, expected.ParentName, expected.ParentId, expected.ParentSub),
            (actual.Id, actual.Length, actual.Kind, actual.LeftOrigin, actual.RightOrigin, actual.ParentName, actual.ParentId, actual.ParentSub));
        Assert.AreEqual(
            (expected.Text, expected.FormatKey, expected.FormatValue, expected.TypeRef, expected.TypeName, expected.DocGuid),
            (actual.Text, actual.FormatKey, actual.FormatValue, actual.TypeRef, actual.TypeName, actual.DocGuid));
        Assert.IsTrue(expected.Content.SequenceEqual(actual.Content), $"the values of {expected.Id}");
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
