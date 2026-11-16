using Ycs;

// 20,000 objects {a: int, b: [int, string, double], c: string}, as an array's content.
static List<object> Items()
{
    var items = new List<object>();
    for (var i = 0; i < 20000; i++)
    {
        items.Add(new Dictionary<string, object> { ["a"] = i, ["b"] = new List<object> { 1, "x", 2.5 }, ["c"] = "s" + i });
    }

    return items;
}

static long Allocated(Action action)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    action();
    return GC.GetAllocatedBytesForCurrentThread() - before;
}

var source = new YDoc();
source.GetArray("a").Add(Items().Select(YValue.FromObject).ToArray());
var update = source.EncodeStateAsUpdate();
for (var warm = 0; warm < 3; warm++)
{
    new YDoc().ApplyUpdate(update);
}

var decoded = new YDoc();
var decode = Allocated(() => decoded.ApplyUpdate(update));
var encode = Allocated(() => decoded.EncodeStateAsUpdate());
var items = Items().Select(YValue.FromObject).ToArray();
var target = new YDoc();
var insert = Allocated(() => target.GetArray("a").Add(items));
Console.WriteLine($"update {update.Length} B; decode {decode} B; encode {encode} B; insert {insert} B");
