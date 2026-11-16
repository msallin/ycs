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
    public void Read_CorpusUpdates_StagingEqualsTheObjects()
    {
        var structs = Sum(DecodedStructTests.CorpusUpdates());

        Assert.IsGreaterThan(500, structs);
    }

    [TestMethod]
    public void Read_EveryContentKind_StagingEqualsTheObjects()
    {
        var inputs = DecodedStructTests.RichUpdates();
        var structs = Sum(inputs);

        Assert.IsGreaterThan(30, structs);
    }

    [TestMethod]
    public void Read_DamagedUpdates_SameExceptionAtTheSameStructAndSoundStaging()
    {
        var seeds = DecodedStructTests.CorpusUpdates().Concat(DecodedStructTests.RichUpdates()).ToList();
        var random = new Random(20261117);
        var inputs = Enumerable.Range(0, 20000).Select(_ => DecodedStructTests.Damage(seeds[random.Next(seeds.Count)], random)).ToList();

        var structs = Sum(inputs);

        Assert.IsGreaterThan(1500, structs);
    }

    [TestMethod]
    public void Read_TooManyStructs_FailsAsWithoutStaging()
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
        AssertShadows(outer);
    }

    [TestMethod]
    public void Lease_Disposed_StagingIsResetAndRefusedAndTheNextLeaseStartsEmpty()
    {
        var first = DecodedStructTests.RichUpdates()[0];
        var second = DecodedStructTests.CorpusUpdates()[0];
        var lease = UpdateReader.Read(first, 0, first.Length, v2: false, Unlimited);
        lease.Dispose();

        using var next = UpdateReader.Read(second, 0, second.Length, v2: false, Unlimited);

        Assert.AreEqual((true, next.Structs.Values.Sum(l => l.Count)), (Throws(() => _ = lease.Staging), next.Staging.Count));
        AssertShadows(next);
    }

    [TestMethod]
    public void Lease_MalformedUpdate_ReleasesTheReaderWithAnEmptyTable()
    {
        var bad = new byte[] { 1, 1, 7, 0, 1, 200 };
        Assert.Throws<YUpdateException>(() => UpdateReader.Read(bad, 0, bad.Length, v2: false, Unlimited));
        var good = DecodedStructTests.RichUpdates()[0];

        using var lease = UpdateReader.Read(good, 0, good.Length, v2: false, Unlimited);

        Assert.AreEqual(lease.Structs.Values.Sum(l => l.Count), lease.Staging.Count);
    }

    [TestMethod]
    public void LazyStructReader_WithStaging_StagesSkipsToo()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        doc.UpdateV2 += (_, e) => updates.Add(e.data);
        for (var i = 0; i < 4; i++)
        {
            doc.GetArray("a").Insert(0, new List<object> { i });
        }

        var gapped = UpdateOperations.MergeUpdatesV2(new[] { updates[0], updates[2] });
        var table = new StagingTable();
        var reader = new LazyStructReader(new UpdateDecoderV2(new Lib0Reader(gapped), Unlimited), filterSkips: true, table);
        while (reader.Next() != null)
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

    private static void AssertShadows(UpdateReader.Lease lease)
    {
        foreach (var (client, list) in lease.Structs)
        {
            var (start, n) = lease.Staging.RangeOf(lease.Staging.FindClient(client));
            var detached = UpdateOperations.Detach(list);
            Assert.AreEqual(list.Count, n);
            for (var k = 0; k < n; k++)
            {
                StagingDifferential.AssertEqual(detached[k], StructStaging.Decoded(lease.Staging, start + k));
            }
        }
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
