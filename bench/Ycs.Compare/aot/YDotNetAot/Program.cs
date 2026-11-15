using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;

using var a = new Doc(new DocOptions { Id = 1 });
var aText = a.Text("text");
var aMap = a.Map("map");
using (var tx = a.WriteTransaction())
{
    aText.Insert(tx, 0, "Hello, world");
    using var n = Input.Long(42);
    aMap.Insert(tx, "n", n);
}

byte[] update;
using (var tx = a.ReadTransaction())
{
    update = tx.StateDiffV1(null);
}

using var b = new Doc(new DocOptions { Id = 2 });
var bText = b.Text("text");
var bMap = b.Map("map");
var events = 0;
using var subscription = bText.Observe(_ => events++);
using (var tx = b.WriteTransaction())
{
    tx.ApplyV1(update);
}

string text;
long value;
using (var tx = b.ReadTransaction())
{
    text = bText.String(tx);
    value = bMap.Get(tx, "n")!.Long;
}

Console.WriteLine($"{text} {value} events={events} v1={update.Length}");
return text == "Hello, world" && events == 1 ? 0 : 1;
