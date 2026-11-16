// Applies an update written by real Yjs, edits the text, encodes the document back in both
// update formats and checks that a fresh document reads the same content. Run as a Native AOT
// binary, this covers the code paths that reflection-based JSON or 'dynamic' would break:
// the 'any' encoding of map values and the JSON of formatting attributes and embeds.

using Ycs;

// yjs 13.6.32, Y.encodeStateAsUpdate: a map with every scalar type, a nested map and array,
// a bold run and an image embed in a text, and a delete set (see tests/Ycs.Tests/YjsInteropTests.cs).
const string YjsUpdateV1 = "ART60JjOBAAoAQNtYXADc3RyAXcQaGVsbG8g4pyTINC80LjRgCgBA21hcANpbnQBfSooAQNtYXAFZmxvYXQBfEBgAAAoAQNtYXAEYm9vbAF4KAEDbWFwBG51bGwBfigBA21hcARqc29uAXYCAWF9AQFidQJ3AXh3AXkjAQNtYXADYmluBAECA/onAQNtYXAGbmVzdGVkASgA+tCYzgQHBGRlZXABdwV2YWx1ZQgBBWFycmF5AXcBYYH60JjOBAkBiPrQmM4ECgJ5dgEBa3cBdof60JjOBAwACAD60JjOBA0CdwJpMHcCaTEEAQR0ZXh0BnBsYWluIIb60JjOBBUEYm9sZAR0cnVlhPrQmM4EFgRib2xkhvrQmM4EGgRib2xkBG51bGzE+tCYzgQa+tCYzgQbBSB0YWlshfrQmM4EGxF7ImltYWdlIjoieC5wbmcifQH60JjOBAEKAQ==";

var doc = new YDoc();
doc.ApplyUpdate(Convert.FromBase64String(YjsUpdateV1));

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
text.Format(0, 3, new Dictionary<string, YValue> { ["italic"] = true, ["size"] = 1.5 });
doc.GetMap("map").Set("added", new Dictionary<string, object> { ["n"] = 42, ["list"] = new object?[] { "x", 2.5, null } });

doc.GetMap("map").Set("observed", 1);
Check(observed > 0 && deepObserved == 2 && !kept.IsEmpty, $"events: observed {observed}, deep {deepObserved}, update {kept.Length} bytes");

// Undo with a time provider and a subdocument's meta (options records, the capture timeout, the stack event payload).
using var undo = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero, TimeProvider = TimeProvider.System });
var undone = 0;
undo.StackItemPopped += (_, e) => undone += e.ChangedParentTypes.Count;
text.Insert(0, "!");
Check(undo.CanUndo && undo.Undo() != null && undone == 1 && text.ToString() == "AOT plain bold tail", $"undo: {text}");
doc.GetMap("map").Set("sub", new YDoc(new YDocOptions { Meta = YValue.FromObject(new Dictionary<string, object> { ["k"] = 1 }) }));

foreach (var v2 in new[] { false, true })
{
    var update = v2 ? doc.EncodeStateAsUpdate(format: UpdateFormat.V2) : doc.EncodeStateAsUpdate();
    var copy = new YDoc();
    if (v2)
    {
        copy.ApplyUpdate(update, format: UpdateFormat.V2);
    }
    else
    {
        copy.ApplyUpdate(update);
    }

    var copyText = copy.GetText("text");
    Check(copyText.ToString() == "AOT plain bold tail", $"v{(v2 ? 2 : 1)}: unexpected text '{copyText}'");
    Check(copyText.ToDelta()[0].Attributes?["italic"] == YValue.True, $"v{(v2 ? 2 : 1)}: formatting lost");
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

// The text API: embeds, deltas of both kinds, a snapshot with ychange, the span and writer reads.
var edited = new YDoc();
var note = edited.GetText("note");
note.Insert(0, "ab");
note.InsertEmbed(1, YValue.Object(new KeyValuePair<string, YValue>("image", "x.png")));
var before = edited.CreateSnapshot();
note.ApplyDelta([new Delta.Retain(1), new Delta.Insert("Z") { Attributes = new Dictionary<string, YValue> { ["bold"] = true } }, new Delta.Delete(1)]);
var runs = note.ToDelta();
Check(runs.Count == 3 && runs[1] is Delta.Insert { Value.Kind: YValueKind.String } && runs[1].Attributes!["bold"] == YValue.True, "text delta lost");
var changes = note.ToDelta(edited.CreateSnapshot(), before, (kind, id) => new YChange(kind, id.Client));
Check(changes.Any(d => d.Attributes?["ychange"]["type"] == "added"), "ychange lost");
Span<char> chars = stackalloc char[note.Length];
Check(note.CopyTo(chars) == 3 && chars[..3].SequenceEqual("aZb".AsSpan()) && note.ToString() == "aZb", "text read lost");

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
