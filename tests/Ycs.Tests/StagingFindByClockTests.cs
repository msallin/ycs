using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StagingFindByClockTests
{
    // Client 1: structs of length 3, 1, 2 from clock 5 (5..7, 8, 9..10); client 2: one struct at 0..3; client 3 named by an origin only.
    private static StagingTable Table()
    {
        var table = new StagingTable();
        table.BeginClient(1, 5);
        table.Add(StagedKind.GC, 3);
        table.Add(StagedKind.Skip, 1);
        table.Add(StagedKind.GC, 2);
        table.BeginClient(2, 0);
        table.Add(StagedKind.String, 4, new ItemId(3, 7));
        return table;
    }

    [TestMethod]
    [DataRow(1, 5L, 0)]
    [DataRow(1, 7L, 0)]
    [DataRow(1, 8L, 1)]
    [DataRow(1, 9L, 2)]
    [DataRow(1, 10L, 2)]
    [DataRow(2, 3L, 3)]
    [DataRow(1, 4L, -1)]
    [DataRow(1, 11L, -1)]
    [DataRow(2, 4L, -1)]
    public void FindByClock_ClockInOrOutsideTheRange_ReturnsTheStructOrMinusOne(long client, long clock, int expected)
    {
        var table = Table();
        var hint = 0;

        var found = table.FindByClock(table.FindClient(client), clock, ref hint);

        Assert.AreEqual(expected, found);
    }

    [TestMethod]
    public void FindByClock_ClientNamedByAnOriginOnly_ReturnsMinusOne()
    {
        var table = Table();
        var hint = 0;

        Assert.AreEqual(-1, table.FindByClock(table.FindClient(3), 7, ref hint));
    }

    [TestMethod]
    public void FindByClock_AnyHintAndAnyOrder_FindsTheSameStructs()
    {
        var table = Table();
        var client = table.FindClient(1);
        var mismatches = 0;

        foreach (var hint0 in new[] { -5, 0, 1, 2, 3, 99 })
        {
            var hint = hint0;
            var found = new[] { 10L, 5, 9, 8, 6, 10, 7 }.Select(clock => table.FindByClock(client, clock, ref hint)).ToArray();
            mismatches += found.SequenceEqual(new[] { 2, 0, 2, 1, 0, 2, 0 }) ? 0 : 1;
        }

        Assert.AreEqual(0, mismatches);
    }
}
