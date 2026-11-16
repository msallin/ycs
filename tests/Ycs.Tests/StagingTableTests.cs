#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StagingTableTests
{
    private static void AssertSound(StagingTable table) => Assert.AreEqual(0, table.Violations().Count, string.Join("; ", table.Violations()));

    private static int AddItem(StagingTable table, StagedKind kind, int length)
    {
        var i = table.Add(kind, length, new ItemId(9, 0));
        return i;
    }

    private static int AddRootItem(StagingTable table, StagedKind kind, int length)
    {
        var i = table.Add(kind, length);
        table.SetParentRoot(i, "root");
        return i;
    }

    private static T Field<T>(StagingTable table, string name) => (T)typeof(StagingTable).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!;

    private static void SetField(StagingTable table, string name, object value) => typeof(StagingTable).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(table, value);

    [TestMethod]
    public void StructBytes_IsTheSizeOfTheStruct()
    {
        Assert.AreEqual(StagingTable.StructBytes, Marshal.SizeOf<StagedStruct>());
    }

    [TestMethod]
    public void Add_String_ReadsBackIdLengthCharsOriginsAndSub()
    {
        var table = new StagingTable();
        table.BeginClient(7, 100);
        var i = table.Add(StagedKind.String, 3, new ItemId(7, 99), new ItemId(5, 2));
        "abc".AsSpan().CopyTo(table.Chars(i));
        table.SetParentSub(i, "k");

        var read = (table.IdOf(i), table.At(i).Length, table.At(i).Kind, table.At(i).Countable, new string(table.Chars(i)), table.LeftOrigin(i), table.RightOrigin(i), table.ParentSub(i), table.At(i).ParentKind);

        Assert.AreEqual((new ItemId(7, 100), 3, StagedKind.String, true, "abc", (ItemId?)new ItemId(7, 99), (ItemId?)new ItemId(5, 2), "k", StagedParent.None), read);
        AssertSound(table);
    }

    [TestMethod]
    public void Add_Any_ReadsBackValues()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = AddRootItem(table, StagedKind.Any, 3);
        var values = table.Values(i);
        values[0] = 1L;
        values[1] = "two";
        values[2] = null;

        var read = table.Values(i).ToArray();

        CollectionAssert.AreEqual(new object?[] { 1L, "two", null }, read);
        AssertSound(table);
    }

    [TestMethod]
    public void Add_TwoStringsAndAnys_KeepTheirOwnRanges()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var a = table.Add(StagedKind.String, 2, new ItemId(1, 0));
        var b = table.Add(StagedKind.Any, 2, new ItemId(1, 1));
        var c = table.Add(StagedKind.String, 3, new ItemId(1, 2));
        table.Chars(a).Fill('a');
        table.Chars(c).Fill('c');
        table.Values(b).Fill(5);

        Assert.AreEqual(("aa", "ccc", 5, 5), (new string(table.Chars(a)), new string(table.Chars(c)), table.Values(b)[0], table.Values(b)[1]));
        AssertSound(table);
    }

    [TestMethod]
    public void Add_GCAndSkipAndDeleted_ReadBackWithoutContent()
    {
        var table = new StagingTable();
        table.BeginClient(3, 10);
        var gc = table.Add(StagedKind.GC, 4);
        var skip = table.Add(StagedKind.Skip, 6);
        var deleted = table.Add(StagedKind.Deleted, 2, new ItemId(3, 9));

        var read = (table.IdOf(gc), table.At(gc).Kind, table.IdOf(skip), table.At(skip).Kind, table.IdOf(deleted), table.At(deleted).Kind, table.At(deleted).Countable, table.At(skip).Countable);

        Assert.AreEqual((new ItemId(3, 10), StagedKind.GC, new ItemId(3, 14), StagedKind.Skip, new ItemId(3, 20), StagedKind.Deleted, false, false), read);
        AssertSound(table);
    }

    [TestMethod]
    public void SetParentRoot_AndSetParentId_ReadBack()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var root = AddRootItem(table, StagedKind.Deleted, 1);
        var nested = table.Add(StagedKind.Deleted, 1);
        table.SetParentId(nested, new ItemId(8, 4));

        Assert.AreEqual((StagedParent.Root, "root", StagedParent.Id, new ItemId(8, 4)), (table.At(root).ParentKind, table.ParentName(root), table.At(nested).ParentKind, table.ParentId(nested)));
        AssertSound(table);
    }

    [TestMethod]
    [DataRow(6, false)]
    [DataRow(5, true)]
    [DataRow(3, true)]
    [DataRow(2, true)]
    [DataRow(9, true)]
    public void SetCold_EachColdKind_ReadsBackTheObject(int kindNumber, bool countable)
    {
        var kind = (StagedKind)kindNumber;
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var obj = new object();
        var i = AddRootItem(table, kind, 1);
        table.SetCold(i, obj);

        Assert.AreEqual((true, kind, countable), (ReferenceEquals(obj, table.Cold(i)), table.At(i).Kind, table.At(i).Countable));
        AssertSound(table);
    }

    [TestMethod]
    public void SetType_WithAndWithoutName_ReadsBackTheReferenceAndName()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var element = AddRootItem(table, StagedKind.Type, 1);
        table.SetType(element, 3, "div");
        var map = table.Add(StagedKind.Type, 1, new ItemId(1, 0));
        table.SetType(map, 1, null);

        Assert.AreEqual((3, "div", 1, (string?)null), (table.At(element).TypeRef, table.TypeName(element), table.At(map).TypeRef, table.TypeName(map)));
        AssertSound(table);
    }

    [TestMethod]
    public void RangeOf_TwoClients_AreContiguousInClockOrder()
    {
        var table = new StagingTable();
        table.BeginClient(5, 0);
        table.Add(StagedKind.GC, 2);
        table.Add(StagedKind.GC, 3);
        table.BeginClient(2, 40);
        table.Add(StagedKind.GC, 1);

        var a = table.FindClient(5);
        var b = table.FindClient(2);

        Assert.AreEqual(((0, 2), (2, 1), 5L, 2L, new ItemId(5, 2), new ItemId(2, 40)), (table.RangeOf(a), table.RangeOf(b), table.ClientAt(a), table.ClientAt(b), table.IdOf(1), table.IdOf(2)));
        AssertSound(table);
    }

    [TestMethod]
    public void RangeOf_ClientOnlyNamedByAnOrigin_IsEmptyAndFindable()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        AddItem(table, StagedKind.Deleted, 1);

        Assert.AreEqual(((0, 0), -1), (table.RangeOf(table.FindClient(9)), table.FindClient(1234)));
        AssertSound(table);
    }

    [TestMethod]
    public void BeginClient_ClientTwice_Throws()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        table.Add(StagedKind.GC, 1);
        table.BeginClient(2, 0);
        table.Add(StagedKind.GC, 1);

        Assert.ThrowsExactly<InvalidOperationException>(() => table.BeginClient(1, 5));
    }

    [TestMethod]
    public void Add_WithoutBeginClient_Throws()
    {
        var table = new StagingTable();

        Assert.ThrowsExactly<InvalidOperationException>(() => table.Add(StagedKind.GC, 1));
    }

    [TestMethod]
    public void Add_ManyStructsAndClients_GrowsAndStaysSound()
    {
        var table = new StagingTable();
        for (var c = 0; c < 100; c++)
        {
            table.BeginClient(c * 3, 0);
            for (var k = 0; k < 50; k++)
            {
                var i = table.Add(StagedKind.String, 4, new ItemId(c * 3, 0));
                table.Chars(i).Fill((char)('a' + k % 26));
            }
        }

        Assert.AreEqual((5000, 100, 'a', 'z'), (table.Count, table.ClientCount, table.Chars(0)[0], table.Chars(4999)[0] == 'x' ? 'z' : table.Chars(4999)[0]));
        AssertSound(table);
    }

    [TestMethod]
    public void Reset_SmallTable_KeepsItsBuffersAndBumpsTheVersion()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = AddRootItem(table, StagedKind.String, 100);
        table.Chars(i).Fill('x');
        var before = table.CapacityBytes;
        var version = table.Version;

        table.Reset();

        Assert.AreEqual((before, version + 1, 0, 0), (table.CapacityBytes, table.Version, table.Count, table.ClientCount));
        AssertSound(table);
    }

    [TestMethod]
    public void Reset_BuffersBeyondTheCap_AreDropped()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = AddRootItem(table, StagedKind.String, StagingTable.RetainedBytesCap);
        table.Chars(i).Fill('x');

        table.Reset();

        Assert.IsTrue(table.CapacityBytes <= StagingTable.RetainedBytesCap, $"{table.CapacityBytes}");
        AssertSound(table);
    }

    [TestMethod]
    public void Reset_ManyBuffers_KeepsTotalUnderTheCap()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        for (var k = 0; k < 2000; k++)
        {
            var i = AddRootItem(table, StagedKind.Any, 8);
            table.Values(i).Fill(k);
        }

        table.Reset();

        Assert.IsTrue(table.CapacityBytes <= StagingTable.RetainedBytesCap, $"{table.CapacityBytes}");
    }

    [TestMethod]
    public void Reset_ManyClients_DropsTheClientList()
    {
        var table = new StagingTable();
        for (var c = 0; c < 2000; c++)
        {
            table.BeginClient(c, 0);
            table.Add(StagedKind.GC, 1);
        }

        table.Reset();
        table.BeginClient(5, 0);
        table.Add(StagedKind.GC, 1);

        Assert.AreEqual((1, 0), (table.ClientCount, table.FindClient(5)));
        AssertSound(table);
    }

    [TestMethod]
    public void Reset_ThenReuse_StartsEmptyAndAllowsTheSameClients()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = AddRootItem(table, StagedKind.Any, 2);
        table.Values(i).Fill("v");
        table.Reset();

        table.BeginClient(1, 50);
        var j = AddRootItem(table, StagedKind.Any, 2);

        Assert.AreEqual((new ItemId(1, 50), 0, null), (table.IdOf(j), j, table.Values(j)[0]));
        AssertSound(table);
    }

    [TestMethod]
    public void Reset_DropsObjectReferences()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = AddRootItem(table, StagedKind.Any, 1);
        var weak = new WeakReference(new object());
        table.Values(i)[0] = weak.Target;
        table.SetParentSub(i, "s");

        table.Reset();

        Assert.AreEqual(0, table.Violations().Count);
        Assert.IsTrue(Field<object?[]>(table, "_values").All(v => v == null) && Field<object?[]>(table, "_refs").All(v => v == null));
    }

    [TestMethod]
    public void Reset_RefKeptAcrossIt_ReadsPoisonInDebugAndTheOldValueInRelease()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        table.Add(StagedKind.String, 2, new ItemId(1, 0));
        ref readonly var kept = ref table.At(0);
        var chars = table.Chars(0);
        chars.Fill('q');

        table.Reset();

#if DEBUG
        Assert.AreNotEqual(2, kept.Length);
        Assert.AreEqual('\uDEAD', chars[0]);
#else
        Assert.AreEqual(2, kept.Length);
        Assert.AreEqual('q', chars[0]);
#endif
    }

    [TestMethod]
    public void Violations_FreshTable_IsEmpty()
    {
        Assert.AreEqual(0, new StagingTable().Violations().Count);
    }

    private static StagingTable Sample()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var s = table.Add(StagedKind.String, 2, new ItemId(1, 0));
        table.Chars(s).Fill('a');
        var a = AddRootItem(table, StagedKind.Any, 2);
        table.Values(a).Fill(1);
        var f = AddRootItem(table, StagedKind.Format, 1);
        table.SetCold(f, new object());
        var b = AddRootItem(table, StagedKind.Any, 1);
        table.Values(b).Fill(2);
        table.BeginClient(2, 0);
        table.Add(StagedKind.GC, 3);
        AssertSound(table);
        return table;
    }

    private static StagedStruct[] Structs(StagingTable table) => Field<StagedStruct[]>(table, "_structs");

    private static IEnumerable<(string Name, Action<StagingTable> Corrupt)> CorruptionList()
    {
        yield return ("clock gap", t => Structs(t)[1].Clock += 1);
        yield return ("zero length", t => Structs(t)[2].Length = 0);
        yield return ("client outside the list", t => Structs(t)[0].Client = 9);
        yield return ("wrong client in the range", t => Structs(t)[4].Client = 0);
        yield return ("origin outside the list", t => Structs(t)[0].OriginClient = 9);
        yield return ("unknown kind", t => Structs(t)[0].Info |= 0xEE << 8);
        yield return ("countable flipped", t => Structs(t)[0].Info ^= 1);
        yield return ("string payload moved", t => Structs(t)[0].Payload = 1);
        yield return ("values payload moved", t => Structs(t)[1].Payload = 1);
        yield return ("item without parent or origin", t => Structs(t)[1].Info &= ~(3 << 1));
        yield return ("item with origin and parent", t => Structs(t)[0].Info |= (int)StagedParent.Root << 1);
        yield return ("GC with a parent sub", t => Structs(t)[4].ParentSub = 0);
        yield return ("bad parent id client", t => { Structs(t)[1].Info = Structs(t)[1].Info & ~(3 << 1) | (int)StagedParent.Id << 1; Structs(t)[1].ParentClient = 9; });
        yield return ("parent name not a string", t => Field<object?[]>(t, "_refs")[0] = new object());
        yield return ("cold object missing", t => Field<object?[]>(t, "_refs")[1] = null);
        yield return ("payload on a GC", t => Structs(t)[4].Payload = 0);
        yield return ("type reference unknown", t => { Structs(t)[2].Info = Structs(t)[2].Info & 0xFFFF | 7 << 8 | 9 << 16; });
        yield return ("chars used too large", t => SetField(t, "_charsUsed", 3));
        yield return ("values used too large", t => SetField(t, "_valuesUsed", 4));
        yield return ("object count off", t => SetField(t, "_refsUsed", 1));
        yield return ("count beyond buffer", t => SetField(t, "_count", 1000));
        yield return ("client index missing", t => Field<Dictionary<long, int>>(t, "_clientIndex").Remove(2));
        yield return ("range shifted", t => Field<int[]>(t, "_rangeStart")[1] = 1);
        yield return ("value not cleared past the used part", t => Field<object?[]>(t, "_values")[Field<object?[]>(t, "_values").Length - 1] = new object());
        yield return ("object not cleared past the used part", t => Field<object?[]>(t, "_refs")[Field<object?[]>(t, "_refs").Length - 1] = new object());
    }

    public static IEnumerable<int> CorruptionIndexes() => Enumerable.Range(0, CorruptionList().Count());

    [TestMethod]
    [DynamicData(nameof(CorruptionIndexes))]
    public void Violations_InjectedCorruption_IsDetected(int index)
    {
        var (name, corrupt) = CorruptionList().ElementAt(index);
        var table = Sample();
        Assert.IsTrue(Field<object?[]>(table, "_values").Length > 2 && Field<object?[]>(table, "_refs").Length > 3, "the sample needs spare capacity: " + name);

        corrupt(table);

        Assert.IsTrue(table.Violations().Count > 0, name);
    }
}
