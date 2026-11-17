using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Random concurrent text editing on 2-4 replicas, differential against real Yjs. The operations
/// and Yjs' results are pinned in Fixtures/text-differential.json, written by
/// interop/differential/gen.mjs: each seed is a sequence of inserts and deletes on a replica and of
/// deliveries of one replica's update to another, in random (out of causal) order. Ycs runs the same
/// operations with the same client ids, so its replicas must converge to Yjs' text, and re-encoding
/// Yjs' final document must give Yjs' bytes.
/// </summary>
[TestClass]
public class TextDifferentialTests
{
    private static readonly Lazy<JsonDocument> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-differential.json"))));

    public static IEnumerable<object[]> Seeds =>
        Fixture.Value.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[] { c.GetProperty("seed").GetInt32() });

    [TestMethod]
    public void FixtureComesFromYjs13()
    {
        StringAssert.StartsWith(Fixture.Value.RootElement.GetProperty("yjs").GetString(), "13.");
    }

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void ReplicasConvergeToYjsText(int seed)
    {
        var testCase = Fixture.Value.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("seed").GetInt32() == seed);
        var clientIds = testCase.GetProperty("clientIds").EnumerateArray().Select(e => e.GetInt32()).ToList();
        var docs = clientIds.Select(id => new YDoc(new YDocOptions { ClientId = id })).ToList();
        var log = new List<byte[]>();
        var remote = new object();

        foreach (var doc in docs)
        {
            doc.GetText("text");
            doc.Updated += (_, e) =>
            {
                if (!ReferenceEquals(e.Origin, remote))
                {
                    log.Add(e.Update.ToArray());
                }
            };
        }

        var step = 0;
        foreach (var op in testCase.GetProperty("ops").EnumerateArray())
        {
            switch (op.GetProperty("op").GetString())
            {
                case "insert":
                    docs[op.GetProperty("replica").GetInt32()].GetText("text").Insert(op.GetProperty("index").GetInt32(), op.GetProperty("text").GetString());
                    break;
                case "delete":
                    docs[op.GetProperty("replica").GetInt32()].GetText("text").Remove(op.GetProperty("index").GetInt32(), op.GetProperty("length").GetInt32());
                    break;
                case "deliver":
                    docs[op.GetProperty("to").GetInt32()].ApplyUpdate(log[op.GetProperty("update").GetInt32()], remote);
                    break;
                default:
                    Assert.Fail($"unknown op at step {step}");
                    break;
            }

            step++;
        }

        Assert.AreEqual(testCase.GetProperty("updates").GetInt32(), log.Count, "every local edit emits one update");
        var expected = testCase.GetProperty("text").GetString();
        for (int i = 0; i < docs.Count; i++)
        {
            Assert.AreEqual(expected, docs[i].GetText("text").ToString(), $"seed {seed}: replica {i} differs from Yjs");
        }

        // Yjs' own encoding of the final document reads back and re-encodes byte for byte.
        var yjsFinal = Convert.FromBase64String(testCase.GetProperty("finalUpdateV1").GetString());
        var fromYjs = new YDoc();
        fromYjs.ApplyUpdate(yjsFinal);
        Assert.AreEqual(expected, fromYjs.GetText("text").ToString());
        CollectionAssert.AreEqual(yjsFinal, fromYjs.EncodeStateAsUpdate(), $"seed {seed}: v1 re-encoding differs from Yjs");
    }
}
