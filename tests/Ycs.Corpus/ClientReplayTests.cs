using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Replays the scripted sessions of the corpus (gen.mjs' Sim.ops) with a Ycs document per client:
/// the same changes, the same client ids, the same deliveries, with the updates the Ycs clients
/// produce themselves (their formatting cleanups included). Every client must end up byte for byte
/// where its Yjs counterpart did. The observer tests cannot see what a client does with its own
/// formats when a concurrent one arrives; these can.
/// </summary>
[TestClass]
public class ClientReplayTests
{
    public static IEnumerable<object[]> ScriptedCases =>
        CorpusCase.All.Where(c => c.Script != null).Select(c => new object[] { c.Name });

    [TestMethod]
    public void Corpus_HasScriptedCases() => Assert.IsGreaterThanOrEqualTo(8, ScriptedCases.Count());

    [TestMethod]
    [DynamicData(nameof(ScriptedCases))]
    public void ReplayAsClients_EveryClient_BytesEqualYjs(string name) =>
        KnownFailures.Check(name, nameof(ReplayAsClients_EveryClient_BytesEqualYjs), () =>
        {
            var c = CorpusCase.Named(name);
            var (docs, log) = Replay(c);

            for (var i = 0; i < docs.Length; i++)
            {
                var expected = c.Clients[i];
                Assert.AreEqual(CanonicalJson.Write(CanonicalJson.FromJson(expected.Content)), Observed.Content(docs[i], c.Roots), $"content of client {expected.Id}");
                Assert.AreEqual(Convert.ToBase64String(expected.State.V1), Convert.ToBase64String(docs[i].EncodeStateV1()), $"encodeStateAsUpdate of client {expected.Id}");
                Assert.AreEqual(Convert.ToBase64String(expected.State.V2), Convert.ToBase64String(docs[i].EncodeStateV2()), $"encodeStateAsUpdateV2 of client {expected.Id}");
            }

            Assert.AreEqual(c.Updates.Count, log.Count, "updates the clients produced (Yjs' log has one per change and per cleanup that changed something)");
        });

    private static (CorpusDoc[] Docs, List<byte[]> Log) Replay(CorpusCase c)
    {
        var log = new List<byte[]>();
        var docs = c.Clients.Select(client => CorpusDoc.Create(c.Gc, client.Id)).ToArray();
        foreach (var doc in docs)
        {
            doc.LocalUpdateV1 += log.Add;
        }

        foreach (var step in c.Script)
        {
            if (step.Ops != null)
            {
                docs[step.Client].EditText(step.Ops);
            }
            else
            {
                docs[step.To].ApplyV1(log[step.Deliver]);
            }
        }

        return (docs, log);
    }
}
