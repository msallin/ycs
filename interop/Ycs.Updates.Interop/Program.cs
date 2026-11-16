// Reads the JSON produced by interop/js/check-updates.mjs, runs it through Ycs'
// YUpdate (merge/diff/state-vector/convert), and writes the results back as base64
// so the JS side can verify them against real Yjs.
// Usage: dotnet run -- input.json > output.json
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

static byte[] B64ToBytes(string s) => Convert.FromBase64String(s);
static string BytesToB64(byte[] b) => Convert.ToBase64String(b);

static List<byte[]> ReadUpdates(JsonNode token) =>
    token.AsArray().Select(t => B64ToBytes(t.GetValue<string>())).ToList();

// --- Scenario 1: merge updates from several independent docs (inserts, deletes, and a GC'd doc). ---

var s1 = input["scenario1"];
var s1UpdatesV1 = ReadUpdates(s1["updatesV1"]);
var s1UpdatesV2 = ReadUpdates(s1["updatesV2"]);

var s1MergedV1 = YUpdate.Merge([.. s1UpdatesV1], format: UpdateFormat.V1);
var s1MergedV2 = YUpdate.Merge([.. s1UpdatesV2], format: UpdateFormat.V2);

var s1SvFromMergedV1 = YUpdate.EncodeStateVector(s1MergedV1, format: UpdateFormat.V1);
var s1SvFromMergedV2 = YUpdate.EncodeStateVector(s1MergedV2, format: UpdateFormat.V2);

// --- Scenario 2: sequential updates from one doc, merged, then diffed against a partial state vector. ---

var s2 = input["scenario2"];
var s2UpdatesV1 = ReadUpdates(s2["updatesV1"]);
var s2UpdatesV2 = ReadUpdates(s2["updatesV2"]);
var s2PartialSvV1 = B64ToBytes(s2["partialStateVectorV1"].GetValue<string>());
var s2PartialSvV2 = B64ToBytes(s2["partialStateVectorV2"].GetValue<string>());

var s2MergedAllV1 = YUpdate.Merge([.. s2UpdatesV1], format: UpdateFormat.V1);
var s2MergedAllV2 = YUpdate.Merge([.. s2UpdatesV2], format: UpdateFormat.V2);

var s2DiffedV1 = YUpdate.Diff(s2MergedAllV1, s2PartialSvV1, format: UpdateFormat.V1);
var s2DiffedV2 = YUpdate.Diff(s2MergedAllV2, s2PartialSvV2, format: UpdateFormat.V2);

var s2ConvertedV1ToV2 = YUpdate.Convert(s2MergedAllV1, UpdateFormat.V1, UpdateFormat.V2);
var s2ConvertedRoundTrip = YUpdate.Convert(s2ConvertedV1ToV2, UpdateFormat.V2, UpdateFormat.V1);

// Exercise (without asserting on) YUpdate.Decode/Describe too, so a regression that
// makes them throw fails this interop check rather than only the unit tests.
var s2Decoded = YUpdate.Decode(s2MergedAllV1, format: UpdateFormat.V1);
var s2Described = YUpdate.Describe(s2MergedAllV1, format: UpdateFormat.V1);
if (string.IsNullOrWhiteSpace(s2Described) || s2Decoded.Structs.Count == 0)
{
    throw new InvalidOperationException("YUpdate.Decode/Describe produced an unexpectedly empty result.");
}

var meta = YUpdate.ParseMeta(s2MergedAllV1, format: UpdateFormat.V1);

Console.WriteLine(JsonSerializer.Serialize(new
{
    scenario1 = new
    {
        mergedV1 = BytesToB64(s1MergedV1),
        mergedV2 = BytesToB64(s1MergedV2),
        stateVectorFromMergedV1 = BytesToB64(s1SvFromMergedV1),
        stateVectorFromMergedV2 = BytesToB64(s1SvFromMergedV2),
    },
    scenario2 = new
    {
        mergedAllV1 = BytesToB64(s2MergedAllV1),
        mergedAllV2 = BytesToB64(s2MergedAllV2),
        diffedV1 = BytesToB64(s2DiffedV1),
        diffedV2 = BytesToB64(s2DiffedV2),
        convertedV1ToV2 = BytesToB64(s2ConvertedV1ToV2),
        convertedV2ToV1RoundTrip = BytesToB64(s2ConvertedRoundTrip),
        parseUpdateMetaFrom = meta.From.ToDictionary(kvp => kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kvp => kvp.Value),
        parseUpdateMetaTo = meta.To.ToDictionary(kvp => kvp.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kvp => kvp.Value),
    },
}, jsonOptions));
