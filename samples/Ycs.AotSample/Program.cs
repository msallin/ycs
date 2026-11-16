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

// Events: a typed observer, a deep observer and the update event, as the generic wrapper of Observe must work without reflection.
var observed = 0;
var deepObserved = 0;
ReadOnlyMemory<byte> kept = default;
using var subscription = text.Observe(e => observed += e.Delta.Count);
using var deepSubscription = doc.GetMap("map").ObserveDeep((events, _) => deepObserved += events[0].Path.Count + events[0].Keys.Count);
doc.Updated += (_, e) => kept = e.Update;

text.Insert(0, "AOT ");
text.Format(0, 3, new Dictionary<string, object> { ["italic"] = true, ["size"] = 1.5 });
doc.GetMap("map").Set("added", new Dictionary<string, object> { ["n"] = 42, ["list"] = new object?[] { "x", 2.5, null } });

doc.GetMap("map").Set("observed", 1);
Check(observed > 0 && deepObserved == 2 && !kept.IsEmpty, $"events: observed {observed}, deep {deepObserved}, update {kept.Length} bytes");

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

    var position = RelativePosition.FromIndex(copyText, 4, -1);
    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
    using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
    {
        position.WriteJson(json);
    }

    using var parsed = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
    var decoded = RelativePosition.Decode(position.Encode());
    Check(decoded.Encode().AsSpan().SequenceEqual(position.Encode()) && RelativePosition.FromJson(parsed.RootElement) == position, $"v{(v2 ? 2 : 1)}: position lost");
    Check(decoded.TryResolve(copy, out var absolute) && absolute.Index == 4, $"v{(v2 ? 2 : 1)}: position resolves elsewhere");
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
