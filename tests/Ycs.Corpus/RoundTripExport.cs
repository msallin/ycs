using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Writes what Ycs encodes, for interop/corpus/roundtrip.mjs to feed back into real Yjs. Does
/// nothing unless YCS_CORPUS_EXPORT names an output directory (interop/corpus/roundtrip.sh sets it).
/// </summary>
[TestClass]
public class RoundTripExport
{
    private const int AuthoredSessions = 10;

    [TestMethod]
    public void Export_WhenRequested_WritesYcsEncodings()
    {
        var dir = Environment.GetEnvironmentVariable("YCS_CORPUS_EXPORT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        Func<byte[], string> b64 = Convert.ToBase64String;

        // Ycs' re-encoding of every corpus case.
        foreach (var c in CorpusCase.All)
        {
            var v1 = CorpusTests.Build(c, c.Updates, v2: false);
            var v2 = CorpusTests.Build(c, c.Updates, v2: true);
            Write(dir, $"case-{c.Name}", new Dictionary<string, object>
            {
                ["case"] = c.Name,
                ["stateV1"] = b64(v1.EncodeStateV1()),
                ["stateV2"] = b64(v2.EncodeStateV2()),
                ["stateVector"] = b64(v1.EncodeStateVector()),
                ["diffV1"] = b64(v1.EncodeStateV1(c.DiffStateVector)),
                ["diffV2"] = b64(v2.EncodeStateV2(c.DiffStateVector)),
                ["mergedV1"] = b64(CorpusUpdates.Merge(c.Updates.Select(u => u.V1).ToList(), v2: false)),
                ["mergedV2"] = b64(CorpusUpdates.Merge(c.Updates.Select(u => u.V2).ToList(), v2: true)),
            });
        }

        // Updates authored by Ycs itself: random multi-client editing sessions.
        for (var seed = 1; seed <= AuthoredSessions; seed++)
        {
            var (log, texts) = ConvergenceTests.Simulate(seed);
            Write(dir, $"authored-{seed}", new Dictionary<string, object>
            {
                ["seed"] = seed,
                ["updates"] = log.Select(b64).ToList(),
                ["text"] = texts[0],
            });
        }
    }

    private static void Write(string dir, string name, Dictionary<string, object> content) =>
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(content));
}
