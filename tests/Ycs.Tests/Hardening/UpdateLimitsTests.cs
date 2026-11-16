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
        YUpdate.Convert(HardeningTests.RunOfGcStructsV2(count, client, clock), UpdateFormat.V2, UpdateFormat.V1, limits: UpdateLimits.Unlimited);

    [TestMethod]
    public void ApplyUpdateV2_DefaultLimit_Accepts131072Structs()
    {
        var doc = new YDoc();

        doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(131_072), format: UpdateFormat.V2);

        Assert.AreEqual(131_072, doc.Store.GetState(1));
    }

    [TestMethod]
    public void ApplyUpdateV2_DefaultLimit_Rejects131073Structs()
    {
        var doc = new YDoc();

        AssertRejected(doc, () => doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(131_073), format: UpdateFormat.V2));
    }

    [TestMethod]
    public void ApplyUpdateV1_StructsBeyondTheLimit_AreRejected()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxStructsPerUpdate = 10_000 } });

        AssertRejected(doc, () => doc.ApplyUpdate(GcRunV1(10_001)));
    }

    [TestMethod]
    public void ApplyUpdateV1_StructsWithinTheLimit_Apply()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxStructsPerUpdate = 10_000 } });

        doc.ApplyUpdate(GcRunV1(10_000));

        Assert.AreEqual(10_000, doc.Store.GetState(1));
    }

    [TestMethod]
    public void ApplyUpdateV2_LimitBelowOneStructPerByte_IsEnforced()
    {
        // 15 structs in 19 bytes, beyond a limit of 10.
        var update = HardeningTests.RunOfGcStructsV2(15);
        Assert.IsGreaterThan(15, update.Length);
        var doc = new YDoc(new YDocOptions { Limits = TenStructs });

        AssertRejected(doc, () => doc.ApplyUpdate(update, format: UpdateFormat.V2));
    }

    [TestMethod]
    public void ApplyUpdate_LimitsOfTheCall_OverrideTheDocument()
    {
        var doc = new YDoc(new YDocOptions { Limits = TenStructs });

        doc.ApplyUpdate(GcRunV1(20), null, limits: UpdateLimits.Unlimited);

        Assert.AreEqual(20, doc.Store.GetState(1));
    }

    [TestMethod]
    public void TryApplyUpdate_BeyondTheLimitsOfTheCall_ReturnsFalse()
    {
        var doc = new YDoc();

        Assert.IsFalse(doc.TryApplyUpdate(GcRunV1(20), origin: null, limits: TenStructs));
    }

    [TestMethod]
    public void UpdateOperations_BeyondTheLimit_EveryFunctionRejects()
    {
        var v1 = GcRunV1(20);
        var v2 = HardeningTests.RunOfGcStructsV2(20);
        var sv = new byte[] { 0 };
        var calls = new List<(string Name, Action Call)>
        {
            ("MergeUpdates", () => YUpdate.Merge([v1, v1], format: UpdateFormat.V1, limits: TenStructs)),
            ("MergeUpdatesV2", () => YUpdate.Merge([v2, v2], format: UpdateFormat.V2, limits: TenStructs)),
            ("DiffUpdate", () => YUpdate.Diff(v1, sv, format: UpdateFormat.V1, limits: TenStructs)),
            ("DiffUpdateV2", () => YUpdate.Diff(v2, sv, format: UpdateFormat.V2, limits: TenStructs)),
            ("EncodeStateVectorFromUpdate", () => YUpdate.EncodeStateVector(v1, format: UpdateFormat.V1, limits: TenStructs)),
            ("EncodeStateVectorFromUpdateV2", () => YUpdate.EncodeStateVector(v2, format: UpdateFormat.V2, limits: TenStructs)),
            ("ParseUpdateMeta", () => YUpdate.ParseMeta(v1, format: UpdateFormat.V1, limits: TenStructs)),
            ("ParseUpdateMetaV2", () => YUpdate.ParseMeta(v2, format: UpdateFormat.V2, limits: TenStructs)),
            ("ConvertUpdateFormatV1ToV2", () => YUpdate.Convert(v1, UpdateFormat.V1, UpdateFormat.V2, limits: TenStructs)),
            ("ConvertUpdateFormatV2ToV1", () => YUpdate.Convert(v2, UpdateFormat.V2, UpdateFormat.V1, limits: TenStructs)),
            ("DecodeUpdate", () => YUpdate.Decode(v1, format: UpdateFormat.V1, limits: TenStructs)),
            ("DecodeUpdateV2", () => YUpdate.Decode(v2, format: UpdateFormat.V2, limits: TenStructs)),
            ("DescribeUpdateV2", () => YUpdate.Describe(v2, format: UpdateFormat.V2, limits: TenStructs)),
            ("SnapshotContainsUpdate", () => new YDoc().CreateSnapshot().ContainsUpdate(v1, limits: TenStructs)),
            ("SnapshotContainsUpdateV2", () => new YDoc().CreateSnapshot().ContainsUpdate(v2, UpdateFormat.V2, limits: TenStructs)),
        };

        var accepted = calls.Where(c => !Throws(c.Call)).Select(c => c.Name).ToList();

        Assert.AreEqual(string.Empty, string.Join(", ", accepted));
    }

    [TestMethod]
    public void UpdateOperations_DefaultLimit_RejectsARunLengthBomb()
    {
        var bomb = HardeningTests.RunOfGcStructsV2(2_097_152);

        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Convert(bomb, UpdateFormat.V2, UpdateFormat.V1));
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

        doc.ApplyUpdate(state, origin: null, limits: UpdateLimits.Unlimited);

        Assert.AreEqual(1_000_000, doc.GetText("t").Length);
    }

    [TestMethod]
    public void ApplyUpdateV2_PendingStructsBeyondTheLimit_AreRejected()
    {
        // Each update is 600 GC structs waiting for clock 0 of its client: a few bytes, many structs.
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1), format: UpdateFormat.V2);

        AssertRejected(doc, () => doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(600, clientId: 1001, clock: 1), format: UpdateFormat.V2));
    }

    [TestMethod]
    public void ApplyUpdateV2_DefaultPendingLimit_KeepsTheStateSmall()
    {
        var doc = new YDoc();
        var accepted = 0;
        for (var client = 1000; client < 1006; client++)
        {
            accepted += doc.TryApplyUpdate(HardeningTests.RunOfGcStructsV2(100_000, client, clock: 1), format: UpdateFormat.V2) ? 1 : 0;
        }

        Assert.AreEqual(1, accepted);
    }

    [TestMethod]
    public void ApplyUpdateV2_SamePendingUpdateTwice_IsMergedAway()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        var update = HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1);
        doc.ApplyUpdate(update, format: UpdateFormat.V2);

        doc.ApplyUpdate(update, format: UpdateFormat.V2);

        Assert.AreEqual(600, doc.Store.PendingStructCount);
    }

    [TestMethod]
    public void ApplyUpdateV2_PendingThatIntegrates_IsReadUnderItsCount()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingStructs = 1_000 } });
        doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(600, clientId: 1000, clock: 1), format: UpdateFormat.V2);

        doc.ApplyUpdate(HardeningTests.RunOfGcStructsV2(2, clientId: 1000, clock: 0), format: UpdateFormat.V2);

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
        var trigger = YUpdate.Merge([small.First, big.Second], format: UpdateFormat.V1);

        AssertRejected(doc, () => doc.ApplyUpdate(trigger));
    }

    [TestMethod]
    public void ApplyUpdate_RetryThatBringsASmallOrphan_IsKept()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingBytes = 10_000 } });
        var small = TwoInserts(200, "a", "b");
        var other = TwoInserts(1000, "a", "c");
        doc.ApplyUpdate(small.Second);

        doc.ApplyUpdate(YUpdate.Merge([small.First, other.Second], format: UpdateFormat.V1));

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
