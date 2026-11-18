using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StructStagingTests
{
    private const long Unlimited = long.MaxValue;

    [TestMethod]
    public void Read_CorpusUpdates_WrittenAndReadAgainGiveTheSameStructs()
    {
        var structs = Sum(DecodedStructTests.CorpusUpdates());

        Assert.IsGreaterThan(500, structs);
    }

    [TestMethod]
    public void Read_EveryContentKind_WrittenAndReadAgainGiveTheSameStructs()
    {
        var inputs = DecodedStructTests.RichUpdates();
        var structs = Sum(inputs);

        Assert.IsGreaterThan(30, structs);
    }

    [TestMethod]
    public void Read_DamagedUpdates_LeaveSoundStagingAndWhatReadsRoundTrips()
    {
        var seeds = DecodedStructTests.CorpusUpdates().Concat(DecodedStructTests.RichUpdates()).ToList();
        var random = new Random(20261117);
        var inputs = Enumerable.Range(0, 20000).Select(_ => DecodedStructTests.Damage(seeds[random.Next(seeds.Count)], random)).ToList();

        var structs = Sum(inputs);

        Assert.IsGreaterThan(1500, structs);
    }

    [TestMethod]
    public void Read_TooManyStructs_LeavesSoundStaging()
    {
        var update = DecodedStructTests.RichUpdates()[0];

        var structs = new[] { 0L, 1, 3, 10 }.Sum(max => StagingDifferential.Check(update, v2: false, max));

        Assert.IsGreaterThan(0, structs);
    }

    [TestMethod]
    public void Lease_ReadWhileAnotherIsOpen_EachHasItsOwnTable()
    {
        var first = DecodedStructTests.RichUpdates()[0];
        var second = DecodedStructTests.CorpusUpdates()[0];
        using var outer = UpdateReader.Read(first, 0, first.Length, v2: false, Unlimited);
        var outerCount = outer.Staging.Count;

        using (var inner = UpdateReader.Read(second, 0, second.Length, v2: false, Unlimited))
        {
            Assert.AreNotSame(outer.Staging, inner.Staging);
        }

        Assert.AreEqual(outerCount, outer.Staging.Count);
        Assert.IsEmpty(outer.Staging.Violations());
        StagingDifferential.AssertSame(ReadTable(first), outer.Staging);
    }

    [TestMethod]
    public void Lease_Disposed_StagingIsResetAndRefusedAndTheNextLeaseStartsEmpty()
    {
        var first = DecodedStructTests.RichUpdates()[0];
        var second = DecodedStructTests.CorpusUpdates()[0];
        var lease = UpdateReader.Read(first, 0, first.Length, v2: false, Unlimited);
        lease.Dispose();

        using var next = UpdateReader.Read(second, 0, second.Length, v2: false, Unlimited);

        Assert.AreEqual((true, ReadTable(second).Count), (Throws(() => _ = lease.Staging), next.Staging.Count));
        StagingDifferential.AssertSame(ReadTable(second), next.Staging);
    }

    [TestMethod]
    public void Lease_MalformedUpdate_ReleasesTheReaderWithAnEmptyTable()
    {
        var bad = new byte[] { 1, 1, 7, 0, 1, 200 };
        Assert.Throws<YUpdateException>(() => UpdateReader.Read(bad, 0, bad.Length, v2: false, Unlimited));
        var good = DecodedStructTests.RichUpdates()[0];

        using var lease = UpdateReader.Read(good, 0, good.Length, v2: false, Unlimited);

        StagingDifferential.AssertSame(ReadTable(good), lease.Staging);
    }

    [TestMethod]
    public void NextStaged_AGappedUpdate_StagesSkipsToo()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        doc.UpdatedV2 += (_, e) => updates.Add(e.Update.ToArray());
        for (var i = 0; i < 4; i++)
        {
            doc.GetArray("a").Insert(0, i);
        }

        var gapped = YUpdate.Merge([updates[0], updates[2]], format: UpdateFormat.V2);
        var table = new StagingTable();
        var decoder = new UpdateDecoderV2(new Lib0Reader(gapped), Unlimited);
        var cursor = EncodingUtils.StructCursor.Begin(ref decoder, gapped, table);
        while (cursor.NextStaged(ref decoder, gapped) >= 0)
        {
        }

        Assert.AreEqual((3, true, 0), (table.Count, Enumerable.Range(0, table.Count).Any(i => table.At(i).Kind == StagedKind.Skip), table.Violations().Count));
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // v1 updates, read by a cursor of its own: what a lease must hold too.
    private static StagingTable ReadTable(byte[] update)
    {
        var table = new StagingTable();
        var decoder = new UpdateDecoderV1(new Lib0Reader(update), Unlimited);
        var cursor = EncodingUtils.StructCursor.Begin(ref decoder, update, table);
        while (cursor.NextStaged(ref decoder, update) >= 0)
        {
        }

        return table;
    }

    private static int Sum(IEnumerable<byte[]> inputs)
    {
        var structs = 0;
        foreach (var input in inputs)
        {
            structs += StagingDifferential.Check(input, v2: false, Unlimited) + StagingDifferential.Check(input, v2: true, Unlimited);
        }

        return structs;
    }
}
