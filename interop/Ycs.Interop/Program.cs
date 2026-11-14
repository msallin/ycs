// Reads the JSON produced by interop/js/gen.mjs, applies the Yjs update with Ycs,
// makes an edit of its own, and writes back what Ycs sees plus its own updates.
// Usage: dotnet run -- js-doc.json > cs-doc.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ycs;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

var input = JsonNode.Parse(File.ReadAllText(args[0]));
var doc = new YDoc();
doc.ApplyUpdateV1(Convert.FromBase64String(input["updateV1"].GetValue<string>()));

// Ycs writes into the same document, the way a .NET server would.
doc.Transact(_ =>
{
    doc.GetMap("map").Set("from-ycs", "written in C#");
    doc.GetText("text").Insert(doc.GetText("text").Length, "!");
});

static object Plain(object value) => value switch
{
    YMap m => m.Keys().ToDictionary(k => k, k => Plain(m.Get(k))),
    YArray a => a.ToArray().Select(Plain).ToList(),
    YText t => t.ToString(),
    // Yjs' toJSON renders a Uint8Array as an object keyed by index; match that shape.
    byte[] b => b.Select((x, i) => (i, x)).ToDictionary(p => p.i.ToString(), p => (object)(int)p.x),
    _ => value
};

var map = doc.GetMap("map").Keys().ToDictionary(k => k, k => Plain(doc.GetMap("map").Get(k)));

Console.WriteLine(JsonSerializer.Serialize(new
{
    readBack = new
    {
        map,
        array = doc.GetArray("array").ToArray().Select(Plain).ToList(),
        text = doc.GetText("text").ToString()
    },
    updateV1 = Convert.ToBase64String(doc.EncodeStateAsUpdateV1()),
    updateV2 = Convert.ToBase64String(doc.EncodeStateAsUpdateV2()),
    // proof the diffing path works too: only what the JS peer is missing
    diffAgainstJsStateVector = Convert.ToBase64String(
        doc.EncodeStateAsUpdateV1(Convert.FromBase64String(input["stateVectorV1"].GetValue<string>())))
}, jsonOptions));
