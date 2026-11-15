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
        ta.EventHandler += (_, _) => { };
        Insert(ta, 0, "abxcde");
        Delete(ta, 2, 1);
        Sync(a, b);
        Insert(tb, 5, "D");
        Sync(b, a);
        Delete(tb, 0, 2);
        var diff = b.EncodeStateAsUpdateV1(a.EncodeStateVectorV1());
        a.Transact(_ =>
        {
            Insert(ta, 5, "p");
            a.ApplyUpdateV1(diff);
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
    [DataRow(true)]
    [DataRow(false)]
    public void RandomEdits_EveryLookupEndsWhereYjsLoopsEnd(bool text)
    {
        for (var seed = 0; seed < 60; seed++)
        {
            var runs = Walks.Select(walk => new Run(text, walk, seed)).ToList();
            for (var step = 0; step < 150; step++)
            {
                var observed = runs.Select(r => r.Step()).ToList();
                Assert.IsTrue(observed.All(o => o == observed[0]), $"seed {seed} step {step}:{Environment.NewLine}{string.Join(Environment.NewLine, observed)}");
            }
        }
    }

    private sealed class Run
    {
        private readonly Random _random;
        private readonly YDoc _a;
        private readonly YDoc _b;
        private readonly AbstractType _ta;
        private readonly AbstractType _tb;
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
                _ta.EventHandler += (_, _) => { };
            }

            // The second window: during the cleanup of a remote transaction, before the list's own observer call.
            _a.GetMap("m").EventHandler += (_, _) =>
            {
                for (; _armed > 0; _armed--)
                {
                    LocalEdit();
                }
            };

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
                    var diff = _b.EncodeStateAsUpdateV1(_a.EncodeStateVectorV1());
                    _a.Transact(_ =>
                    {
                        LocalEdit();
                        _a.ApplyUpdateV1(diff);
                        LocalEdit();
                    });
                    break;
                case 2:
                    _armed = 1 + _random.Next(3);
                    _b.GetMap("m").Set("k", _random.Next());
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
                    _ = _text ? ((YText)_ta).ToDelta().Count : ((YArray)_ta).Get(_random.Next(length));
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

    private static YDoc Doc(long client, string walk)
    {
        var doc = new YDoc(new YDocOptions { ClientId = client });
        doc.LiteralWalkSteps = walk == "eager" ? 1 : doc.LiteralWalkSteps;
        doc.RunHeadIndexDisabledForTests = walk == "plain";
        doc.MarkerLookupsForTests = [];
        return doc;
    }

    private static AbstractType List(YDoc doc, bool text) => text ? doc.GetText("t") : doc.GetArray("t");

    private static int Length(AbstractType type) => type is YText t ? t.Length : ((YArray)type).Length;

    private static void Insert(AbstractType type, int index, string chars)
    {
        if (type is YText text)
        {
            text.Insert(index, chars);
        }
        else
        {
            ((YArray)type).Insert(index, chars.Select(c => (object)c.ToString()).ToList());
        }
    }

    private static void Delete(AbstractType type, int index, int length)
    {
        if (type is YText text)
        {
            text.Delete(index, length);
        }
        else
        {
            ((YArray)type).Delete(index, length);
        }
    }

    private static string Content(AbstractType type) => type is YText text ? text.ToString() : string.Join(",", ((YArray)type).ToArray());

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdateV1(from.EncodeStateAsUpdateV1(to.EncodeStateVectorV1()));

    private static string Lookups(YDoc doc)
    {
        var text = string.Join(" ", doc.MarkerLookupsForTests.Select(l => $"{l.Item.Client}:{l.Item.Clock}@{l.Index}"));
        doc.MarkerLookupsForTests.Clear();
        return text;
    }
}
