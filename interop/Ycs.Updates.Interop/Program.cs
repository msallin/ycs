// Reads the JSON produced by interop/js/check-updates.mjs, runs it through Ycs'
// UpdateOperations (merge/diff/state-vector/convert), and writes the results back as base64
// so the JS side can verify them against real Yjs.
// Usage: dotnet run -- input.json > output.json
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ycs;

var input = JObject.Parse(File.ReadAllText(args[0]));

static byte[] B64ToBytes(string s) => Convert.FromBase64String(s);
static string BytesToB64(byte[] b) => Convert.ToBase64String(b);

static List<byte[]> ReadUpdates(JToken token) =>
    token.Select(t => B64ToBytes(t.Value<string>())).ToList();

// --- Scenario 1: merge updates from several independent docs (inserts, deletes, and a GC'd doc). ---

var s1 = input["scenario1"];
var s1UpdatesV1 = ReadUpdates(s1["updatesV1"]);
var s1UpdatesV2 = ReadUpdates(s1["updatesV2"]);

var s1MergedV1 = UpdateOperations.MergeUpdates(s1UpdatesV1);
var s1MergedV2 = UpdateOperations.MergeUpdatesV2(s1UpdatesV2);

var s1SvFromMergedV1 = UpdateOperations.EncodeStateVectorFromUpdate(s1MergedV1);
var s1SvFromMergedV2 = UpdateOperations.EncodeStateVectorFromUpdateV2(s1MergedV2);

// --- Scenario 2: sequential updates from one doc, merged, then diffed against a partial state vector. ---

var s2 = input["scenario2"];
var s2UpdatesV1 = ReadUpdates(s2["updatesV1"]);
var s2UpdatesV2 = ReadUpdates(s2["updatesV2"]);
var s2PartialSvV1 = B64ToBytes(s2["partialStateVectorV1"].Value<string>());
var s2PartialSvV2 = B64ToBytes(s2["partialStateVectorV2"].Value<string>());

var s2MergedAllV1 = UpdateOperations.MergeUpdates(s2UpdatesV1);
var s2MergedAllV2 = UpdateOperations.MergeUpdatesV2(s2UpdatesV2);

var s2DiffedV1 = UpdateOperations.DiffUpdate(s2MergedAllV1, s2PartialSvV1);
var s2DiffedV2 = UpdateOperations.DiffUpdateV2(s2MergedAllV2, s2PartialSvV2);

var s2ConvertedV1ToV2 = UpdateOperations.ConvertUpdateFormatV1ToV2(s2MergedAllV1);
var s2ConvertedRoundTrip = UpdateOperations.ConvertUpdateFormatV2ToV1(s2ConvertedV1ToV2);

// Exercise (without asserting on) DecodeUpdate/DescribeUpdate/LogUpdate too, so a regression that
// makes them throw fails this interop check rather than only the unit tests.
var s2Decoded = UpdateOperations.DecodeUpdate(s2MergedAllV1);
var s2Described = UpdateOperations.DescribeUpdate(s2MergedAllV1);
if (string.IsNullOrWhiteSpace(s2Described) || s2Decoded.Structs.Count == 0)
{
    throw new InvalidOperationException("DecodeUpdate/DescribeUpdate produced an unexpectedly empty result.");
}

var meta = UpdateOperations.ParseUpdateMeta(s2MergedAllV1);

Console.WriteLine(JsonConvert.SerializeObject(new
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
        parseUpdateMetaFrom = meta.From.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
        parseUpdateMetaTo = meta.To.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
    },
}, Formatting.Indented));
