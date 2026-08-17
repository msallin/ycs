// Companion to interop/js/check-positions.mjs: decodes a Yjs-produced snapshot and a batch of
// Yjs-produced relative positions with Ycs, resolves each relative position to an absolute index
// against the same document, and re-encodes both the snapshot and the positions so the JS side can
// confirm Yjs itself accepts the re-encoded bytes and agrees on the resolved indices.
// Usage: dotnet run -- request.json > response.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ycs;

var input = JObject.Parse(File.ReadAllText(args[0]));

var doc = new YDoc(new YDocOptions { Gc = false });
doc.ApplyUpdateV1(Convert.FromBase64String(input["updateV1"].Value<string>()));

var snapshot = Snapshot.DecodeSnapshot(Convert.FromBase64String(input["snapshotV1"].Value<string>()));

var positions = ((JArray)input["positions"]).Select(p =>
{
    var label = p["label"].Value<string>();
    var encoded = Convert.FromBase64String(p["encoded"].Value<string>());

    var rpos = RelativePosition.DecodeRelativePosition(encoded);
    var abs = AbsolutePosition.CreateAbsolutePositionFromRelativePosition(rpos, doc);

    if (abs == null)
    {
        throw new Exception($"'{label}': could not resolve to an absolute position");
    }

    return new
    {
        label,
        resolvedIndex = abs.Index,
        resolvedAssoc = abs.Assoc,
        reencoded = Convert.ToBase64String(RelativePosition.EncodeRelativePosition(rpos))
    };
}).ToList();

Console.WriteLine(JsonConvert.SerializeObject(new
{
    snapshotReencoded = Convert.ToBase64String(snapshot.EncodeSnapshot()),
    positions
}, Formatting.Indented));
