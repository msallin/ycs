#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Reads an update straight into a <see cref="StagingTable"/> and requires a sound table, whatever the input, and, when
/// the read succeeds, that writing the staged structs and reading them again gives the same structs. Linked into Ycs.Fuzz.
/// </summary>
internal static class StagingDifferential
{
    /// <summary>The number of structs read (up to the one that failed); fails the test on any difference.</summary>
    public static int Check(byte[] input, bool v2, long maxStructs)
    {
        var table = new StagingTable();
        var error = Read(input, v2, maxStructs, table);
        AssertSound(table);
        if (error != null)
        {
            return table.Count;
        }

        var written = Write(table);
        var again = new StagingTable();
        Assert.IsNull(Read(written, v2: true, long.MaxValue, again), "the written structs read again");
        AssertSound(again);
        AssertSame(table, again);
        return table.Count;
    }

    public static void AssertSound(StagingTable table)
    {
        var violations = table.Violations();
        Assert.IsEmpty(violations, string.Join("; ", violations));
    }

    /// <summary>The same structs in the same order.</summary>
    public static void AssertSame(StagingTable expected, StagingTable actual)
    {
        Assert.AreEqual(expected.Count, actual.Count, "the same structs");
        for (var i = 0; i < expected.Count; i++)
        {
            var want = StructStaging.Decoded(expected, i);

            // A lone surrogate cannot be written as UTF-8: it comes back as U+FFFD.
            AssertEqual(want with { Text = want.Text == null ? null : Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(want.Text)) }, StructStaging.Decoded(actual, i));
        }
    }

    /// <summary>The structs section of an update (v2) with every struct of <paramref name="table"/>, in its order.</summary>
    private static byte[] Write(StagingTable table)
    {
        var encoder = UpdateEncoderV2.Rent();
        try
        {
            var writer = new LazyStructWriter(encoder);
            for (var i = 0; i < table.Count; i++)
            {
                LazyStructWriter.WriteStruct(writer, table, i, 0);
            }

            LazyStructWriter.Finish(writer);
            return encoder.ToArray();
        }
        finally
        {
            encoder.Return();
        }
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

    private static string? Read(byte[] input, bool v2, long maxStructs, StagingTable table)
    {
        try
        {
            var reader = new Lib0Reader(input);
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader, maxStructs) : new UpdateDecoderV1(reader, maxStructs);
            var cursor = EncodingUtils.StructCursor.Begin(ref decoder, input, table, []);
            while (cursor.NextStaged(ref decoder, input) >= 0)
            {
            }

            return null;
        }
        catch (Exception e)
        {
            return $"{e.GetType().FullName}: {e.Message}";
        }
    }
}
