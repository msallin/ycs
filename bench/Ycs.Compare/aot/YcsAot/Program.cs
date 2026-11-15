using Ycs;

var a = new YDoc(new YDocOptions { ClientId = 1 });
a.GetText("text").Insert(0, "Hello, world");
a.GetMap("map").Set("n", 42L);
var update = a.EncodeStateAsUpdateV1();

var b = new YDoc(new YDocOptions { ClientId = 2 });
var events = 0;
b.GetText("text").Observe((_, _) => events++);
b.ApplyUpdate(update);
var text = b.GetText("text").ToString();
Console.WriteLine($"{text} {b.GetMap("map").Get("n")} events={events} v1={update.Length}");
return text == "Hello, world" && events == 1 ? 0 : 1;
