using System.Text;

namespace Ycs.Compare;

/// <summary>
/// The workloads on ycs, written as its documentation uses it: implicit transactions per call, <c>Transact</c> to group.
/// Each method mirrors the method of the same name in <see cref="YrsCases"/>.
/// </summary>
internal static class YcsCases
{
    private static YDoc NewDoc(long id = Inputs.ClientId) => new(new YDocOptions { ClientId = id });

    private static Prepared TextResult(YDoc doc, YText text, Action body) =>
        new(body, text.ToString, () => doc.EncodeStateAsUpdateV1(), () => doc.EncodeStateAsUpdateV2(), doc.Destroy);

    public static Prepared TextAppend(Fixtures f)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var chars = Inputs.Typing;
        return TextResult(doc, text, () =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                text.Insert(i, chars[i]);
            }
        });
    }

    public static Prepared TextAppendOneTxn(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var chars = Inputs.Typing.AsSpan(0, n).ToArray();
        return TextResult(doc, text, () => doc.Transact(_ =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                text.Insert(i, chars[i]);
            }
        }));
    }

    /// <summary>As <see cref="TextAppendOneTxn"/>, with a text observer that counts its events (no run is extended in place).</summary>
    public static Prepared TextAppendOneTxnObserved(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var chars = Inputs.Typing.AsSpan(0, n).ToArray();
        long seen = 0;
        text.Observe((_, _) => seen++);
        return new Prepared(
            () => doc.Transact(_ =>
            {
                for (var i = 0; i < chars.Length; i++)
                {
                    text.Insert(i, chars[i]);
                }
            }),
            () => $"{seen} {text}",
            () => doc.EncodeStateAsUpdateV1(),
            () => doc.EncodeStateAsUpdateV2(),
            doc.Destroy);
    }

    public static Prepared EmptyTransactions(Fixtures f)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        return TextResult(doc, text, () =>
        {
            for (var i = 0; i < Inputs.TypingN; i++)
            {
                doc.Transact(static _ => { });
            }
        });
    }

    public static Prepared TextRandom(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var edits = Inputs.RandomTyping.AsSpan(0, n).ToArray();
        return TextResult(doc, text, () =>
        {
            foreach (var e in edits)
            {
                text.Insert(e.Pos, e.Insert);
            }
        });
    }

    public static Prepared TraceReplay(Fixtures f)
    {
        var trace = f.Trace;
        var doc = NewDoc();
        var text = doc.GetText("text");
        return TextResult(doc, text, () =>
        {
            for (var t = 0; t < trace.Transactions; t++)
            {
                var (from, to) = (trace.TxnStarts[t], trace.TxnStarts[t + 1]);
                doc.Transact(_ =>
                {
                    for (var i = from; i < to; i++)
                    {
                        var e = trace.Edits[i];
                        if (e.Delete > 0)
                        {
                            text.Delete(e.Pos, e.Delete);
                        }

                        if (e.Insert.Length > 0)
                        {
                            text.Insert(e.Pos, e.Insert);
                        }
                    }
                });
            }
        });
    }

    public static Prepared Concurrent(Fixtures f)
    {
        var baseDoc = NewDoc(100);
        baseDoc.GetText("text").Insert(0, Inputs.BaseText);
        var baseUpdate = baseDoc.EncodeStateAsUpdateV1();
        var clients = new YDoc[Inputs.Clients];
        var texts = new YText[Inputs.Clients];
        for (var c = 0; c < clients.Length; c++)
        {
            clients[c] = NewDoc(c + 1);
            texts[c] = clients[c].GetText("text");
            clients[c].ApplyUpdate(baseUpdate);
        }

        var baseVector = clients[0].EncodeStateVector();
        for (var c = 0; c < clients.Length; c++)
        {
            foreach (var e in Inputs.ClientEdits[c])
            {
                if (e.Delete > 0)
                {
                    texts[c].Delete(e.Pos, e.Delete);
                }
                else
                {
                    texts[c].Insert(e.Pos, e.Insert);
                }
            }
        }

        return new Prepared(
            () =>
            {
                var diffs = new byte[clients.Length][];
                for (var c = 0; c < clients.Length; c++)
                {
                    diffs[c] = clients[c].EncodeStateAsUpdateV1(baseVector);
                }

                for (var i = 0; i < clients.Length; i++)
                {
                    for (var j = 0; j < clients.Length; j++)
                    {
                        if (i != j)
                        {
                            clients[i].ApplyUpdate(diffs[j]);
                        }
                    }
                }
            },
            () =>
            {
                var first = texts[0].ToString();
                if (texts.Any(t => t.ToString() != first))
                {
                    throw new InvalidOperationException("the clients did not converge");
                }

                return first;
            },
            () => clients[0].EncodeStateAsUpdateV1(),
            () => clients[0].EncodeStateAsUpdateV2(),
            () =>
            {
                foreach (var c in clients)
                {
                    c.Destroy();
                }
            });
    }

    /// <summary>A stored state is trusted input, so it is applied without limits, as docs/api.md recommends.</summary>
    public static Prepared ApplyState(Fixtures f, string file, bool v2)
    {
        var update = f.Bytes(file);
        YDoc last = null;
        return new Prepared(
            () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    last = new YDoc();
                    if (v2)
                    {
                        last.ApplyUpdateV2(update, null, UpdateLimits.Unlimited);
                    }
                    else
                    {
                        last.ApplyUpdateV1(update, null, UpdateLimits.Unlimited);
                    }
                }
            },
            () => last.GetText("text").ToString(),
            () => last.EncodeStateAsUpdateV1(),
            () => last.EncodeStateAsUpdateV2());
    }

    /// <summary>Updates from a peer, at the default limits.</summary>
    public static Prepared ApplyUpdates(Fixtures f, string file, bool oneTxn)
    {
        var updates = f.Updates(file);
        var doc = new YDoc();
        var text = doc.GetText("text");
        void Apply()
        {
            foreach (var u in updates)
            {
                doc.ApplyUpdate(u);
            }
        }

        return TextResult(doc, text, oneTxn ? () => doc.Transact(_ => Apply()) : Apply);
    }

    private static (YDoc Doc, YText Text) Loaded(Fixtures f)
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(f.Bytes("trace.ycs.v1"), null, UpdateLimits.Unlimited);
        return (doc, doc.GetText("text"));
    }

    public static Prepared Encode(Fixtures f, string what)
    {
        var (doc, text) = Loaded(f);
        var halfVector = f.Bytes("trace.half.sv");
        long sink = 0;
        Action body = what switch
        {
            "v1" => () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    sink += doc.EncodeStateAsUpdateV1().Length;
                }
            },
            "v2" => () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    sink += doc.EncodeStateAsUpdateV2().Length;
                }
            },
            "sv" => () =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    sink += doc.EncodeStateVector().Length;
                }
            },
            "diff" => () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    sink += doc.EncodeStateAsUpdateV1(halfVector).Length;
                }
            },
            _ => throw new ArgumentException(what),
        };
        return new Prepared(body, () => text.ToString(), () => doc.EncodeStateAsUpdateV1(), () => doc.EncodeStateAsUpdateV2(), doc.Destroy);
    }

    private static Prepared MapResult(YDoc doc, YMap map, Action body) =>
        new(body, () => Canonical.Map(map.Select(kv => (kv.Key, Convert.ToInt64(kv.Value)))), () => doc.EncodeStateAsUpdateV1(), () => doc.EncodeStateAsUpdateV2(), doc.Destroy);

    public static Prepared MapSetDistinct(Fixtures f, bool oneTxn)
    {
        var doc = NewDoc();
        var map = doc.GetMap("map");
        var keys = Inputs.Keys;
        void Set()
        {
            for (var i = 0; i < keys.Length; i++)
            {
                map.Set(keys[i], (long)i);
            }
        }

        return MapResult(doc, map, oneTxn ? () => doc.Transact(_ => Set()) : Set);
    }

    public static Prepared MapSetSame(Fixtures f)
    {
        var doc = NewDoc();
        var map = doc.GetMap("map");
        return MapResult(doc, map, () =>
        {
            for (var i = 0; i < Inputs.MapN; i++)
            {
                map.Set("key", (long)i);
            }
        });
    }

    private static Prepared ArrayResult(YDoc doc, YArray array, Action body) =>
        new(body, () => Canonical.Array(array.Select(Convert.ToInt64)), () => doc.EncodeStateAsUpdateV1(), () => doc.EncodeStateAsUpdateV2(), doc.Destroy);

    public static Prepared ArrayPush(Fixtures f, bool oneTxn, int n)
    {
        var doc = NewDoc();
        var array = doc.GetArray("array");
        void Push()
        {
            for (var i = 0; i < n; i++)
            {
                array.Add([(long)i]);
            }
        }

        return ArrayResult(doc, array, oneTxn ? () => doc.Transact(_ => Push()) : Push);
    }

    public static Prepared ArrayInsertRandom(Fixtures f)
    {
        var doc = NewDoc();
        var array = doc.GetArray("array");
        var positions = Inputs.ArrayInserts;
        return ArrayResult(doc, array, () =>
        {
            for (var i = 0; i < positions.Length; i++)
            {
                array.Insert(positions[i], [(long)i]);
            }
        });
    }

    /// <summary>One call with every value: one item, on both libraries.</summary>
    private static YArray FilledArray(YDoc doc)
    {
        var array = doc.GetArray("array");
        array.Add([.. Enumerable.Range(0, Inputs.ArrayN).Select(i => (object)(long)i)]);
        return array;
    }

    public static Prepared ArrayDeleteRandom(Fixtures f)
    {
        var doc = NewDoc();
        var array = FilledArray(doc);
        var positions = Inputs.ArrayDeletes;
        return ArrayResult(doc, array, () =>
        {
            foreach (var p in positions)
            {
                array.Delete(p, 1);
            }
        });
    }

    public static Prepared ReadText(Fixtures f)
    {
        var (doc, text) = Loaded(f);
        long chars = 0;
        return new Prepared(
            () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    chars += text.ToString().Length;
                }
            },
            () => $"{chars} {text}",
            () => doc.EncodeStateAsUpdateV1(),
            () => doc.EncodeStateAsUpdateV2(),
            doc.Destroy);
    }

    public static Prepared ReadArray(Fixtures f)
    {
        var doc = NewDoc();
        var array = FilledArray(doc);
        long sum = 0;
        return new Prepared(
            () =>
            {
                for (var r = 0; r < 10; r++)
                {
                    foreach (var v in array)
                    {
                        sum += (long)v;
                    }
                }
            },
            () => sum.ToString(),
            () => doc.EncodeStateAsUpdateV1(),
            () => doc.EncodeStateAsUpdateV2(),
            doc.Destroy);
    }

    public static Prepared ReadMap(Fixtures f)
    {
        var doc = NewDoc();
        var map = doc.GetMap("map");
        doc.Transact(_ =>
        {
            for (var i = 0; i < Inputs.MapN; i++)
            {
                map.Set(Inputs.Keys[i], (long)i);
            }
        });
        long sum = 0;
        return new Prepared(
            () =>
            {
                for (var r = 0; r < 10; r++)
                {
                    foreach (var kv in map)
                    {
                        sum += kv.Key.Length + (long)kv.Value;
                    }
                }
            },
            () => sum.ToString(),
            () => doc.EncodeStateAsUpdateV1(),
            () => doc.EncodeStateAsUpdateV2(),
            doc.Destroy);
    }

    /// <summary>A capture timeout of 0 makes every keystroke its own undo step on both libraries.</summary>
    public static Prepared Undo(Fixtures f, string phase)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var undo = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
        var chars = Inputs.Typing.AsSpan(0, Inputs.UndoN).ToArray();
        void Type()
        {
            for (var i = 0; i < chars.Length; i++)
            {
                text.Insert(i, chars[i]);
            }
        }

        var steps = 0;
        void UndoAll()
        {
            while (undo.Undo() != null)
            {
                steps++;
            }
        }

        void RedoAll()
        {
            while (undo.Redo() != null)
            {
                steps++;
            }
        }

        if (phase != "type")
        {
            Type();
        }

        if (phase == "redo")
        {
            UndoAll();
            steps = 0;
        }

        Action body = phase switch { "type" => Type, "undo" => UndoAll, "redo" => RedoAll, _ => throw new ArgumentException(phase) };
        return new Prepared(body, () => $"{(phase == "type" ? chars.Length : steps)} {text}", () => doc.EncodeStateAsUpdateV1(), () => doc.EncodeStateAsUpdateV2(), doc.Destroy);
    }

    public static Prepared Observe(Fixtures f, bool readDelta)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var chars = Inputs.Typing;
        long seen = 0;
        text.Observe((_, e) => seen += readDelta ? ((YTextEvent)e.Event).Delta.Count : 1);
        return new Prepared(
            () =>
            {
                for (var i = 0; i < chars.Length; i++)
                {
                    text.Insert(i, chars[i]);
                }
            },
            () => $"{seen} {text}",
            () => doc.EncodeStateAsUpdateV1(),
            () => doc.EncodeStateAsUpdateV2(),
            doc.Destroy);
    }

    /// <summary>Replays the trace, collecting the v1 update of each transaction and the state vector halfway.</summary>
    public static (string Text, byte[] V1, byte[] V2, List<byte[]> Updates, byte[] HalfVector) Record(Trace trace)
    {
        var doc = NewDoc();
        var text = doc.GetText("text");
        var updates = new List<byte[]>(trace.Transactions);
        doc.Update += (_, e) => updates.Add(e.data);
        byte[] half = null;
        for (var t = 0; t < trace.Transactions; t++)
        {
            if (t == trace.Transactions / 2)
            {
                half = doc.EncodeStateVector();
            }

            doc.Transact(_ =>
            {
                for (var i = trace.TxnStarts[t]; i < trace.TxnStarts[t + 1]; i++)
                {
                    var e = trace.Edits[i];
                    if (e.Delete > 0)
                    {
                        text.Delete(e.Pos, e.Delete);
                    }

                    if (e.Insert.Length > 0)
                    {
                        text.Insert(e.Pos, e.Insert);
                    }
                }
            });
        }

        return (text.ToString(), doc.EncodeStateAsUpdateV1(), doc.EncodeStateAsUpdateV2(), updates, half);
    }

    /// <summary>The content of a v1 state as each workload's <see cref="Prepared.Content"/> canonicalizes it.</summary>
    public static string ContentOf(byte[] v1, string root)
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(v1, null, UpdateLimits.Unlimited);
        return root switch
        {
            "text" => doc.GetText("text").ToString(),
            "map" => Canonical.Map(doc.GetMap("map").Select(kv => (kv.Key, Convert.ToInt64(kv.Value)))),
            "array" => Canonical.Array(doc.GetArray("array").Select(Convert.ToInt64)),
            _ => throw new ArgumentException(root),
        };
    }
}

internal static class Canonical
{
    public static string Map(IEnumerable<(string Key, long Value)> entries)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            sb.Append(k).Append('=').Append(v).Append('\n');
        }

        return sb.ToString();
    }

    public static string Array(IEnumerable<long> values) => string.Join(',', values);
}
