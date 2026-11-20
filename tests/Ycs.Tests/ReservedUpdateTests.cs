using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>A section of an update reserves room for the structs it announces, as far as the input can hold them.</summary>
[TestClass]
public class ReservedUpdateTests
{
    [TestMethod]
    public void ReadUpdate_ManyStructsOfOneClient_FillsTheStructBufferOnce()
    {
        var source = new YDoc(new YDocOptions { ClientId = 7 });
        var map = source.GetMap("m");
        for (var i = 0; i < 500; i++)
        {
            map["k" + i] = (long)i;
        }

        var update = source.EncodeStateAsUpdate();
        var table = new StagingTable();
        var ds = new DeleteSet();
        var decoder = new UpdateDecoderV1(update, 1 << 20, UpdateLimits.DefaultMaxDepth);

        EncodingUtils.ReadUpdate(ref decoder, update, table, ds, null, null);

        var capacity = ((Array)typeof(StagingTable).GetField("_structs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!).Length;
        Assert.AreEqual((500, 500), (table.Count, capacity));
    }

    [TestMethod]
    public void ReadUpdate_MoreStructsThanAFixedStep_ReservesAllOfThem()
    {
        var source = new YDoc(new YDocOptions { ClientId = 7 });
        var map = source.GetMap("m");
        const int count = 70_000;
        source.Transact(_ =>
        {
            for (var i = 0; i < count; i++)
            {
                map["k" + i] = (long)i;
            }
        });

        var update = source.EncodeStateAsUpdate();
        var table = new StagingTable();
        var decoder = new UpdateDecoderV1(update, 1 << 20, UpdateLimits.DefaultMaxDepth);

        EncodingUtils.ReadUpdate(ref decoder, update, table, new DeleteSet(), null, null);

        var capacity = ((Array)typeof(StagingTable).GetField("_structs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!).Length;
        // A buffer this large is rented from the shared pool, which rounds up to a power of two.
        Assert.AreEqual((count, true), (table.Count, capacity >= count && capacity < 2 * count));
    }
}
