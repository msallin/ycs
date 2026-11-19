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
}
