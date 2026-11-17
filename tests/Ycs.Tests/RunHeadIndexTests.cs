using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The marker walks through the run-head index (<see cref="ClientStructs.RunHead"/>, <c>SeekRight</c>,
/// <c>SeekLeft</c>) against Yjs' loops item by item (<see cref="YDoc.RunHeadIndexDisabledForTests"/>): the same
/// content, the same markers, every lookup ending on the same item with the same index.
/// </summary>
[TestClass]
public class RunHeadIndexTests
{
    // "eager": the index is asked from the first step; "default": after 32 steps; "plain": Yjs' loops.
    private static readonly string[] Walks = ["eager", "default", "plain"];

    /// <summary>
    /// The design review's script, run in Yjs 13.6.32 (which gives "cdeqpD"): after the first insert Yjs' marker sits
    /// on D, 2:0@6, which the walk right reaches by stepping out of client 1's chain when the visible length reaches
    /// the index exactly at the chain's end; the diff applied inside the transaction leaves it stale.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void StaleMarkerAtTheEndOfAChain_AsYjs(bool text)
    {
        var outcomes = Walks.Select(walk => ReviewScript(text, walk)).ToList();

        Assert.AreEqual(text ? "cdeqpD" : "c,d,e,q,p,D", outcomes[0].Content);
        Assert.IsTrue(outcomes.All(o => o == outcomes[0]), string.Join(Environment.NewLine, outcomes));
    }

    private static (string Content, string Markers, string Lookups) ReviewScript(bool text, string walk)
    {
        var a = Doc(1, walk);
        var b = Doc(2, walk);
        var ta = List(a, text);
        var tb = List(b, text);
        ta.ObserveAny(_ => { });
        Insert(ta, 0, "abxcde");
        Delete(ta, 2, 1);
        Sync(a, b);
        Insert(tb, 5, "D");
        Sync(b, a);
        Delete(tb, 0, 2);
        var diff = b.EncodeStateAsUpdate(a.EncodeStateVector());
        a.Transact(_ =>
        {
            Insert(ta, 5, "p");
            a.ApplyUpdate(diff);
            Insert(ta, 5, "q");
        });

        return (Content(ta), ((YArrayBase)ta).DescribeSearchMarkers(), Lookups(a));
    }

    /// <summary>
    /// Random edits of a list two documents share, with long chains (one client's pieces, split by deletes), chains
    /// that end at the other client's item, deleted chain members, and both windows in which Yjs leaves markers stale:
    /// an update applied inside a local transaction, and an observer editing the list during the cleanup of a remote
    /// transaction. After every step each walk gives the same content, markers and lookups.
    /// </summary>
    [TestMethod]
    [DataRow(true, 128)]
    [DataRow(false, 128)]
    [DataRow(true, 4)]
    [DataRow(false, 2)]
    [TestCategory("Slow")]
    public void RandomEdits_EveryLookupEndsWhereYjsLoopsEnd(bool text, int capacity)
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var runs = ClientStructsTests.WithLeafCapacity(capacity, () => Walks.Select(walk => new Run(text, walk, seed)).ToList());
            for (var step = 0; step < 150; step++)
            {
                var observed = runs.Select(r => r.Step()).ToList();
                Assert.IsTrue(observed.All(o => o == observed[0]), $"seed {seed} step {step}:{Environment.NewLine}{string.Join(Environment.NewLine, observed)}");
            }
        }
    }

    /// <summary>
    /// One item of 3,000 values; single deletes split it into 3,000 structs, alternately live and deleted; then
    /// deletes of c live values merge 2c + 1 structs (3 to 81) that end one before, at and one after a leaf boundary,
    /// that start at one and that are centred on one, followed by reads and inserts. After each step the walks give
    /// the same content, markers and lookups, and the list equals a recount.
    /// </summary>
    [TestMethod]
    [DataRow(128)]
    [DataRow(8)]
    [DataRow(4)]
    public void MergesAroundLeafBoundaries_EveryLookupEndsWhereYjsLoopsEnd(int capacity)
    {
        var outcomes = Walks.Select(walk => ClientStructsTests.WithLeafCapacity(capacity, () => Boundaries(walk))).ToList();

        Assert.IsTrue(outcomes.All(o => o == outcomes[0]), string.Join(Environment.NewLine, outcomes.Select(o => o.Length > 4000 ? o[..4000] : o)));
    }

    private static string Boundaries(string walk)
    {
        var log = new System.Text.StringBuilder();
        var doc = Doc(1, walk);
        var a = doc.GetArray("a");
        a.Observe(_ => { });
        a.Insert(0, Enumerable.Range(0, 3000).Select(v => (object)(long)v).ToList().Select(YValue.FromObject).ToArray());
        for (var k = 0; k < 1500; k++)
        {
            a.RemoveRange(k, 1);
        }

        var structs = doc.Store.Clients[1];
        log.Append(structs.Count).Append(' ').Append(Recounted(structs)).Append(' ').Append(Lookups(doc)).Append('\n');
        var random = new Random(5);
        foreach (var pick in new[] { 0, 1, -1, 3 })
        {
            foreach (var c in new[] { 1, 2, 15, 16, 17, 40 })
            {
                foreach (var shape in new[] { -1, 0, 1, 2, 3 })
                {
                    if (a.Length < c + 2)
                    {
                        continue;
                    }

                    var bounds = LeafStarts(structs);
                    var b = bounds.Count == 0 ? 3 : bounds[pick >= 0 ? Math.Min(pick, bounds.Count - 1) : Math.Max(0, bounds.Count + pick)];
                    var first = shape switch { -1 => b - 1 - (2 * c + 1), 0 => b - (2 * c + 1), 1 => b + 1 - (2 * c + 1), 2 => b, _ => b - c };
                    a.RemoveRange(Math.Clamp(first / 2, 0, a.Length - c), c);
                    log.Append(b).Append('/').Append(shape).Append('/').Append(c).Append(": ").Append(structs.Count).Append(' ').Append(Recounted(structs)).Append(' ');
                    for (var k = 0; k < 4; k++)
                    {
                        log.Append(a[random.Next(a.Length)].ToObject()).Append(',');
                        a.Insert(random.Next(a.Length + 1), (long)-k);
                    }

                    log.Append(Lookups(doc)).Append(' ').Append(a.DescribeSearchMarkers()).Append(' ').Append(Recounted(structs)).Append('\n');
                }
            }
        }

        log.Append(string.Join(",", a.ToArray()));
        return log.ToString();
    }

    // The number of structs, after a recount that throws on a difference.
    private static int Recounted(ClientStructs structs) =>
        structs.Violations() is { } violation ? throw new InvalidOperationException(violation) : structs.Count;

    // The positions where the leaves after the first start: the clocks of their first structs, found back by position.
    private static List<int> LeafStarts(ClientStructs structs)
    {
        var starts = new List<int>();
        for (var i = 1; i < structs.Count; i++)
        {
            if (structs.IsLeafStartForTests(i))
            {
                starts.Add(i);
            }
        }

        return starts;
    }

    /// <summary>
    /// A root text that got formats before the document asked for it keeps its search markers (in Yjs the formats'
    /// integration switches off the markers of the placeholder type, not of the YText made later), so its list holds
    /// live formats: inside a client's run and at its head (a break that is a live format, where the walk left
    /// stops), and formats that deletes make dead. After every step each walk gives the same content, markers and
    /// lookups.
    /// </summary>
    [TestMethod]
    [DataRow(128)]
    [DataRow(4)]
    [DataRow(2)]
    public void PlaceholderTextWithFormats_EveryLookupEndsWhereYjsLoopsEnd(int capacity)
    {
        var withMarkers = 0;
        for (var seed = 0; seed < 100; seed++)
        {
            var outcomes = Walks.Select(walk => ClientStructsTests.WithLeafCapacity(capacity, () =>
            {
                var markers = 0;
                var outcome = Placeholder(walk, seed, ref markers);
                withMarkers += markers;
                return outcome;
            })).ToList();
            Assert.IsTrue(outcomes.All(o => o == outcomes[0]), $"seed {seed}:{Environment.NewLine}{string.Join(Environment.NewLine, outcomes)}");
        }

        // The case is reached: lookups in a list that holds formats, with its markers on.
        Assert.IsTrue(withMarkers > 0);
    }

    private static string Placeholder(string walk, int seed, ref int withMarkers)
    {
        var log = new System.Text.StringBuilder();
        var random = new Random(seed);
        var a = Doc(1, walk);
        var b = Doc(2, walk, gc: seed % 3 != 0);
        var ta = a.GetText("t");
        ta.Insert(0, new string('a', 60 + random.Next(60)));
        for (var k = 0; k < 25; k++)
        {
            var length = ta.Length;
            switch (random.Next(4))
            {
                case 0:
                    ta.Insert(random.Next(length + 1), "F", new Dictionary<string, YValue> { [random.Next(2) == 0 ? "b" : "i"] = true });
                    break;
                case 1:
                    var at = random.Next(length);
                    ta.Format(at, Math.Min(1 + random.Next(6), length - at), new Dictionary<string, YValue> { ["b"] = random.Next(3) == 0 ? YValue.Null : true });
                    break;
                case 2:
                    ta.Remove(random.Next(length), 1);
                    break;
                default:
                    ta.Insert(random.Next(length + 1), "p");
                    break;
            }
        }

        // b integrates the formats before it asks for the text.
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var tb = b.GetText("t");
        for (var k = 0; k < 60; k++)
        {
            try
            {
                PlaceholderStep(random, a, b, ta, tb);
            }
            catch (Exception e)
            {
                log.Append(" [").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(']');
            }

            var lookups = Lookups(b);
            var markers = tb.DescribeSearchMarkers();
            if (lookups.Length > 0 && markers != "off")
            {
                withMarkers++;
            }

            log.Append(string.Join("|", tb.ToDelta().Select(d => d.Inserted() + (d.Attributes == null ? "" : "{" + string.Join(",", d.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + "}"))));
            log.Append(" # ").Append(lookups).Append(" # ").Append(markers).Append('\n');
        }

        log.Append(Convert.ToHexString(b.EncodeStateAsUpdate()));
        return log.ToString();
    }

    private static void PlaceholderStep(Random random, YDoc a, YDoc b, YText ta, YText tb)
    {
        var length = tb.Length;
        switch (random.Next(6))
        {
            case 0:
            case 1:
                tb.Insert(random.Next(length + 1), "x");
                break;
            case 2:
            case 3:
            case 4:
                if (length > 0)
                {
                    var at = random.Next(length);
                    tb.Remove(at, Math.Min(1 + random.Next(4), length - at));
                }

                break;
            default:
                // A plain remote edit: no format is integrated, so the markers stay (emptied by the remote transaction).
                Sync(b, a);
                if (ta.Length > 0)
                {
                    ta.Remove(random.Next(ta.Length), 1);
                }

                Sync(a, b);
                break;
        }
    }

    private sealed class Run
    {
        private readonly Random _random;
        private readonly YDoc _a;
        private readonly YDoc _b;
        private readonly YType _ta;
        private readonly YType _tb;
        private readonly bool _text;
        private int _armed;
        private long _clients = 10;
        private string _errors = "";

        public Run(bool text, string walk, int seed)
        {
            _random = new Random(seed);
            _text = text;
            _a = Doc(1, walk);
            _b = Doc(2, walk);
            _ta = List(_a, text);
            _tb = List(_b, text);
            if (seed % 2 == 0)
            {
                _ta.ObserveAny(_ => { });
            }

            // The second window: during the cleanup of a remote transaction, before the list's own observer call.
            _a.GetMap("m").Observe(_ =>
            {
                for (; _armed > 0; _armed--)
                {
                    LocalEdit();
                }
            });

            // A long list in one piece of client 1: the deletes split it into one long chain.
            Insert(_ta, 0, new string('a', 300 + _random.Next(300)));
            Sync(_a, _b);
        }

        public (string Content, string Markers, string Lookups, string Remote) Step()
        {
            switch (_random.Next(10))
            {
                case 0:
                    // The other client inserts (into the middle of a chain: a break) and deletes.
                    Insert(_tb, _random.Next(Length(_tb) + 1), "R");
                    if (Length(_tb) > 1)
                    {
                        Delete(_tb, _random.Next(Length(_tb) - 1), 1);
                    }

                    Sync(_b, _a);
                    break;
                case 1:
                    // The first window: the other client's update applied inside a local transaction.
                    Sync(_a, _b);
                    Insert(_tb, _random.Next(Length(_tb) + 1), "S");
                    var diff = _b.EncodeStateAsUpdate(_a.EncodeStateVector());
                    _a.Transact(_ =>
                    {
                        LocalEdit();
                        _a.ApplyUpdate(diff);
                        LocalEdit();
                    });
                    break;
                case 2:
                    _armed = 1 + _random.Next(3);
                    _b.GetMap("m")["k"] = _random.Next();
                    Sync(_b, _a);
                    _armed = 0;
                    break;
                case 3:
                    _a.Transact(_ =>
                    {
                        for (var k = _random.Next(6); k > 0; k--)
                        {
                            LocalEdit();
                        }
                    });
                    break;
                default:
                    LocalEdit();
                    break;
            }

            // A document that got structs of its own client in a transaction that is not local takes a random client
            // id, as Yjs does: the same in every run instead.
            if (_a.ClientId > 10_000)
            {
                _a.ClientId = ++_clients;
            }

            return (Content(_ta), ((YArrayBase)_ta).DescribeSearchMarkers(), Lookups(_a) + _errors, Content(_tb));
        }

        // An edit after a stale marker can run past the end ("Array length exceeded"), in Yjs as here.
        private void LocalEdit()
        {
            try
            {
                Edit();
            }
            catch (Exception e)
            {
                _errors += " " + e.Message;
            }
        }

        private void Edit()
        {
            var length = Length(_ta);
            switch (_random.Next(4))
            {
                case 0 when length > 0:
                    // Reads walk from the marker too.
                    _ = _text ? ((YText)_ta).ToDelta().Count : ((YArray)_ta)[_random.Next(length)].ToObject();
                    break;
                case 1:
                    Insert(_ta, _random.Next(length + 1), _random.Next(3) == 0 ? "xy" : "x");
                    break;
                default:
                    if (length > 0)
                    {
                        var at = _random.Next(length);
                        Delete(_ta, at, Math.Min(1 + _random.Next(2), length - at));
                    }

                    break;
            }
        }
    }

    private static YDoc Doc(long client, string walk, bool gc = true)
    {
        var doc = new YDoc(new YDocOptions { ClientId = client, GarbageCollection = gc });
        doc.LiteralWalkStepsForTests = walk == "eager" ? 1 : doc.LiteralWalkStepsForTests;
        doc.RunHeadIndexDisabledForTests = walk == "plain";
        doc.MarkerLookupsForTests = [];
        return doc;
    }

    private static YType List(YDoc doc, bool text) => text ? doc.GetText("t") : doc.GetArray("t");

    private static int Length(YType type) => type is YText t ? t.Length : ((YArray)type).Length;

    private static void Insert(YType type, int index, string chars)
    {
        if (type is YText text)
        {
            text.Insert(index, chars);
        }
        else
        {
            ((YArray)type).Insert(index, chars.Select(c => (object)c.ToString()).ToList().Select(YValue.FromObject).ToArray());
        }
    }

    private static void Delete(YType type, int index, int length)
    {
        if (type is YText text)
        {
            text.Remove(index, length);
        }
        else
        {
            ((YArray)type).RemoveRange(index, length);
        }
    }

    private static string Content(YType type) => type is YText text ? text.ToString() : string.Join(",", ((YArray)type).ToArray());

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()));

    private static string Lookups(YDoc doc)
    {
        var text = string.Join(" ", doc.MarkerLookupsForTests.Select(l => $"{l.Item.Client}:{l.Item.Clock}@{l.Index}"));
        doc.MarkerLookupsForTests.Clear();
        return text;
    }
}
