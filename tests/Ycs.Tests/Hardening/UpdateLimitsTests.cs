using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="UpdateLimits"/>: how many structs an update may hold (v1 and v2), how many and how
/// many bytes may wait for missing structs (also when an update lets them integrate), and that
/// an application can raise them for input it trusts.
/// </summary>
[TestClass]
public class UpdateLimitsTests
{
    private static readonly UpdateLimits TenStructs = new() { MaxStructsPerUpdate = 10 };

    private static string StateOf(YDoc doc) =>
        doc.GetText("t").ToString() + "|" + Convert.ToHexString(doc.EncodeStateVector()) + "|" + Convert.ToHexString(doc.EncodeStateAsUpdate());

    private static void AssertRejected(YDoc doc, Action apply)
    {
        var before = StateOf(doc);
        Assert.ThrowsExactly<YUpdateException>(apply);
        Assert.AreEqual(before, StateOf(doc));
    }

    private static byte[] GcRunV1(uint count, int client = 1, uint clock = 0) =>
        UpdateOperations.ConvertUpdateFormatV2ToV1(HardeningTests.RunOfGcStructsV2(count, client, clock), UpdateLimits.Unlimited);

    [TestMethod]
    public void ApplyUpdateV2_DefaultLimit_Accepts131072Structs()
    {
        var doc = new YDoc();

        doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(131_072));

        Assert.AreEqual(131_072, doc.Store.GetState(1));
    }

    [TestMethod]
    public void ApplyUpdateV2_DefaultLimit_Rejects131073Structs()
    {
        var doc = new YDoc();

        AssertRejected(doc, () => doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(131_073)));
    }

    [TestMethod]
    public void ApplyUpdateV1_StructsBeyondTheLimit_AreRejected()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxStructsPerUpdate = 10_000 } });

        AssertRejected(doc, () => doc.ApplyUpdateV1(GcRunV1(10_001)));
    }

    [TestMethod]
    public void ApplyUpdateV1_StructsWithinTheLimit_Apply()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxStructsPerUpdate = 10_000 } });

        doc.ApplyUpdateV1(GcRunV1(10_000));

        Assert.AreEqual(10_000, doc.Store.GetState(1));
    }

    [TestMethod]
    public void ApplyUpdateV2_LimitBelowOneStructPerByte_IsEnforced()
    {
        // 15 structs in 19 bytes, beyond a limit of 10.
        var update = HardeningTests.RunOfGcStructsV2(15);
        Assert.IsGreaterThan(15, update.Length);
        var doc = new YDoc(new YDocOptions { Limits = TenStructs });

        AssertRejected(doc, () => doc.ApplyUpdateV2(update));
    }

    [TestMethod]
    public void ApplyUpdate_LimitsOfTheCall_OverrideTheDocument()
    {
        var doc = new YDoc(new YDocOptions { Limits = TenStructs });

        doc.ApplyUpdateV1(GcRunV1(20), transactionOrigin: null, UpdateLimits.Unlimited);

        Assert.AreEqual(20, doc.Store.GetState(1));
    }

    [TestMethod]
    public void TryApplyUpdate_BeyondTheLimitsOfTheCall_ReturnsFalse()
    {
        var doc = new YDoc();

        Assert.IsFalse(doc.TryApplyUpdate(GcRunV1(20), transactionOrigin: null, TenStructs));
    }

    [TestMethod]
    public void UpdateOperations_BeyondTheLimit_EveryFunctionRejects()
    {
        var v1 = GcRunV1(20);
        var v2 = HardeningTests.RunOfGcStructsV2(20);
        var sv = new byte[] { 0 };
        var calls = new List<(string Name, Action Call)>
        {
            ("MergeUpdates", () => UpdateOperations.MergeUpdates(new[] { v1, v1 }, TenStructs)),
            ("MergeUpdatesV2", () => UpdateOperations.MergeUpdatesV2(new[] { v2, v2 }, TenStructs)),
            ("DiffUpdate", () => UpdateOperations.DiffUpdate(v1, sv, TenStructs)),
            ("DiffUpdateV2", () => UpdateOperations.DiffUpdateV2(v2, sv, TenStructs)),
            ("EncodeStateVectorFromUpdate", () => UpdateOperations.EncodeStateVectorFromUpdate(v1, TenStructs)),
            ("EncodeStateVectorFromUpdateV2", () => UpdateOperations.EncodeStateVectorFromUpdateV2(v2, TenStructs)),
            ("ParseUpdateMeta", () => UpdateOperations.ParseUpdateMeta(v1, TenStructs)),
            ("ParseUpdateMetaV2", () => UpdateOperations.ParseUpdateMetaV2(v2, TenStructs)),
            ("ConvertUpdateFormatV1ToV2", () => UpdateOperations.ConvertUpdateFormatV1ToV2(v1, TenStructs)),
            ("ConvertUpdateFormatV2ToV1", () => UpdateOperations.ConvertUpdateFormatV2ToV1(v2, TenStructs)),
            ("DecodeUpdate", () => UpdateOperations.DecodeUpdate(v1, TenStructs)),
            ("DecodeUpdateV2", () => UpdateOperations.DecodeUpdateV2(v2, TenStructs)),
            ("DescribeUpdateV2", () => UpdateOperations.DescribeUpdateV2(v2, TenStructs)),
            ("SnapshotContainsUpdate", () => new YDoc().CreateSnapshot().SnapshotContainsUpdate(v1, TenStructs)),
            ("SnapshotContainsUpdateV2", () => new YDoc().CreateSnapshot().SnapshotContainsUpdateV2(v2, TenStructs)),
        };

        var accepted = calls.Where(c => !Throws(c.Call)).Select(c => c.Name).ToList();

        Assert.AreEqual(string.Empty, string.Join(", ", accepted));
    }

    [TestMethod]
    public void UpdateOperations_DefaultLimit_RejectsARunLengthBomb()
    {
        var bomb = HardeningTests.RunOfGcStructsV2(2_097_152);

        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.ConvertUpdateFormatV2ToV1(bomb));
    }

    private static bool Throws(Action call)
    {
        try
        {
            call();
            return false;
        }
        catch (YUpdateException)
        {
            return true;
        }
    }

    [TestMethod]
    public void ApplyUpdate_TrustedMillionStructDocument_AppliesWithUnlimitedLimits()
    {
        // Each character prepended is an item of its own (it cannot merge with the one after it).
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("t");
        source.Transact(_ =>
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                text.Insert(0, "x");
            }
        });
        var state = source.EncodeStateAsUpdate();
        Assert.IsFalse(new YDoc().TryApplyUpdate(state), "the default limit rejects it");
        var doc = new YDoc();

        doc.ApplyUpdate(state, transactionOrigin: null, UpdateLimits.Unlimited);

        Assert.AreEqual(1_000_000, doc.GetText("t").Length);
    }

    [TestMethod]
    public void ApplyUpdateV2_PendingStructsBeyondTheLimit_AreRejected()
    {
        // Each update is 600 GC structs waiting for clock 0 of its client: a few bytes, many structs.
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1));

        AssertRejected(doc, () => doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(600, clientId: 1001, clock: 1)));
    }

    [TestMethod]
    public void ApplyUpdateV2_DefaultPendingLimit_KeepsTheStateSmall()
    {
        var doc = new YDoc();
        var accepted = 0;
        for (var client = 1000; client < 1006; client++)
        {
            accepted += doc.TryApplyUpdateV2(HardeningTests.RunOfGcStructsV2(100_000, client, clock: 1)) ? 1 : 0;
        }

        Assert.AreEqual(1, accepted);
    }

    [TestMethod]
    public void ApplyUpdateV2_SamePendingUpdateTwice_IsMergedAway()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        var update = HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1);
        doc.ApplyUpdateV2(update);

        doc.ApplyUpdateV2(update);

        Assert.AreEqual(600, doc.Store.PendingStructCount);
    }

    [TestMethod]
    public void ApplyUpdateV2_PendingThatIntegrates_IsReadUnderItsCount()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1));

        doc.ApplyUpdateV2(HardeningTests.RunOfGcStructsV2(2, clientId: 1000, clock: 0));

        Assert.AreEqual(601, doc.Store.GetState(1000));
    }

    /// <summary>Two updates of client <paramref name="client"/>: <paramref name="first"/> at 0, then <paramref name="second"/> after it.</summary>
    private static (byte[] First, byte[] Second) TwoInserts(long client, string first, string second)
    {
        var doc = new YDoc(new YDocOptions { ClientId = client });
        var updates = new List<byte[]>();
        doc.Updated += (_, e) => updates.Add(e.Update.ToArray());
        doc.GetText("t").Insert(0, first);
        doc.GetText("t").Insert(first.Length, second);
        return (updates[0], updates[1]);
    }

    [TestMethod]
    public void ApplyUpdate_RetryThatBringsALargeOrphan_IsRejected()
    {
        // The trigger lets the small update integrate and brings 100 KB that waits for another client.
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingBytes = 10_000 } });
        var small = TwoInserts(200, "a", "b");
        var big = TwoInserts(1000, "a", new string('x', 100_000));
        doc.ApplyUpdate(small.Second);
        var trigger = UpdateOperations.MergeUpdates(new[] { small.First, big.Second });

        AssertRejected(doc, () => doc.ApplyUpdate(trigger));
    }

    [TestMethod]
    public void ApplyUpdate_RetryThatBringsASmallOrphan_IsKept()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingBytes = 10_000 } });
        var small = TwoInserts(200, "a", "b");
        var other = TwoInserts(1000, "a", "c");
        doc.ApplyUpdate(small.Second);

        doc.ApplyUpdate(UpdateOperations.MergeUpdates(new[] { small.First, other.Second }));

        Assert.AreEqual("ab", doc.GetText("t").ToString());
        Assert.IsTrue(doc.HasPending);
    }

    [TestMethod]
    public void UpdateLimits_NotPositive_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UpdateLimits { MaxStructsPerUpdate = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UpdateLimits { MaxPendingStructs = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new UpdateLimits { MaxPendingBytes = -1 });
    }

    [TestMethod]
    public void YDocOptions_NullLimits_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new YDocOptions { Limits = null });
    }
}
