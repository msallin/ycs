// Companion to interop/js/check-positions.mjs: decodes a Yjs-produced snapshot and a batch of
// Yjs-produced relative positions with Ycs, resolves each relative position to an absolute index
// against the same document, and re-encodes both the snapshot and the positions so the JS side can
// confirm Yjs itself accepts the re-encoded bytes and agrees on the resolved indices.
// Usage: dotnet run -- request.json > response.json
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

var doc = new YDoc(new YDocOptions { Gc = false });
doc.ApplyUpdateV1(Convert.FromBase64String(input["updateV1"].GetValue<string>()));

var snapshot = Snapshot.DecodeSnapshot(Convert.FromBase64String(input["snapshotV1"].GetValue<string>()));

var positions = input["positions"].AsArray().Select(p =>
{
    var label = p["label"].GetValue<string>();
    var encoded = Convert.FromBase64String(p["encoded"].GetValue<string>());

    var rpos = RelativePosition.Decode(encoded);

    if (!rpos.TryResolve(doc, out var abs))
    {
        throw new Exception($"'{label}': could not resolve to an absolute position");
    }

    return new
    {
        label,
        resolvedIndex = abs.Index,
        resolvedAssoc = abs.Assoc,
        reencoded = Convert.ToBase64String(rpos.Encode())
    };
}).ToList();

Console.WriteLine(JsonSerializer.Serialize(new
{
    snapshotReencoded = Convert.ToBase64String(snapshot.EncodeSnapshot()),
    positions
}, jsonOptions));
