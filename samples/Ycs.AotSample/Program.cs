// Applies an update written by real Yjs, edits the text, encodes the document back in both
// update formats and checks that a fresh document reads the same content. Run as a Native AOT
// binary, this covers the code paths that reflection-based JSON or 'dynamic' would break:
// the 'any' encoding of map values and the JSON of formatting attributes and embeds.

using Ycs;

// yjs 13.6.32, Y.encodeStateAsUpdate: a map with every scalar type, a nested map and array,
// a bold run and an image embed in a text, and a delete set (see tests/Ycs.Tests/YjsInteropTests.cs).
const string YjsUpdateV1 = "ART60JjOBAAoAQNtYXADc3RyAXcQaGVsbG8g4pyTINC80LjRgCgBA21hcANpbnQBfSooAQNtYXAFZmxvYXQBfEBgAAAoAQNtYXAEYm9vbAF4KAEDbWFwBG51bGwBfigBA21hcARqc29uAXYCAWF9AQFidQJ3AXh3AXkjAQNtYXADYmluBAECA/onAQNtYXAGbmVzdGVkASgA+tCYzgQHBGRlZXABdwV2YWx1ZQgBBWFycmF5AXcBYYH60JjOBAkBiPrQmM4ECgJ5dgEBa3cBdof60JjOBAwACAD60JjOBA0CdwJpMHcCaTEEAQR0ZXh0BnBsYWluIIb60JjOBBUEYm9sZAR0cnVlhPrQmM4EFgRib2xkhvrQmM4EGgRib2xkBG51bGzE+tCYzgQa+tCYzgQbBSB0YWlshfrQmM4EGxF7ImltYWdlIjoieC5wbmcifQH60JjOBAEKAQ==";

var doc = new YDoc();
doc.ApplyUpdateV1(Convert.FromBase64String(YjsUpdateV1));

var text = doc.GetText("text");
Check(text.ToString() == "plain bold tail", $"unexpected text '{text}'");

text.Insert(0, "AOT ");
text.Format(0, 3, new Dictionary<string, object> { ["italic"] = true, ["size"] = 1.5 });
doc.GetMap("map").Set("added", new Dictionary<string, object> { ["n"] = 42, ["list"] = new object?[] { "x", 2.5, null } });

foreach (var v2 in new[] { false, true })
{
    var update = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1();
    var copy = new YDoc();
    if (v2)
    {
        copy.ApplyUpdateV2(update);
    }
    else
    {
        copy.ApplyUpdateV1(update);
    }

    var copyText = copy.GetText("text");
    Check(copyText.ToString() == "AOT plain bold tail", $"v{(v2 ? 2 : 1)}: unexpected text '{copyText}'");
    Check(copyText.ToDelta()[0].Attributes?["italic"] is true, $"v{(v2 ? 2 : 1)}: formatting lost");
    var added = (IDictionary<string, object>)copy.GetMap("map").Get("added");
    Check(added["n"] is 42, $"v{(v2 ? 2 : 1)}: map value lost");
    Console.WriteLine($"v{(v2 ? 2 : 1)}: {update.Length} bytes, text '{copyText}'");
}

Console.WriteLine("OK");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine("FAIL: " + message);
        Environment.Exit(1);
    }
}
