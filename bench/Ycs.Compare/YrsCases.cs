namespace Ycs.Compare;

using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;
using YDotNet.Document.Transactions;
using YDotNet.Document.Types.Maps;
using YDotNet.Document.Types.Texts;
using YArray = YDotNet.Document.Types.Arrays.Array;
using YUndoManager = YDotNet.Document.UndoManagers.UndoManager;
using YUndoManagerOptions = YDotNet.Document.UndoManagers.UndoManagerOptions;

/// <summary>
/// The workloads on YDotNet 0.7.0 (yrs 0.26), written as its README and tests use it: an explicit write transaction
/// per change (disposed, which commits), a read transaction per read, root types fetched before any transaction is
/// open, inputs disposed after use (which skips their finalizer). Integers are written with <c>Input.Double</c>, which
/// yrs encodes as a Yjs number, as ycs does a <c>long</c>; <c>Input.Long</c> would write a bigint (README.md). Each method mirrors the method of the same name
/// in <see cref="YcsCases"/>.
/// </summary>
internal static class YrsCases
{
    private static Doc NewDoc(long id = Inputs.ClientId) => new(new DocOptions { Id = (ulong)id });

    public static string Text(Doc doc, Text text)
    {
        using var tx = doc.ReadTransaction();
        return text.String(tx);
    }

    public static byte[] StateV1(Doc doc)
    {
        using var tx = doc.ReadTransaction();
        return tx.StateDiffV1(null);
    }

    public static byte[] StateV2(Doc doc)
    {
        using var tx = doc.ReadTransaction();
        return tx.StateDiffV2(null);
    }

    private static Prepared TextResult(Doc doc, Text text, Action body) =>
        new(body, () => Text(doc, text), () => StateV1(doc), () => StateV2(doc), doc.Dispose);

    public static Prepared TextAppend(Fixtures f) => TextAppend(f, DocEncoding.Utf16);

    public static Prepared TextAppend(Fixtures f, DocEncoding encoding)
    {
        var doc = new Doc(new DocOptions { Id = (ulong)Inputs.ClientId, Encoding = encoding });
        var text = doc.Text("text");
        var chars = Inputs.Typing;
        return TextResult(doc, text, () =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                using var tx = doc.WriteTransaction();
                text.Insert(tx, (uint)i, chars[i]);
            }
        });
    }

    public static Prepared TextAppendOneTxn(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        var chars = Inputs.Typing.AsSpan(0, n).ToArray();
        return TextResult(doc, text, () =>
        {
            using var tx = doc.WriteTransaction();
            for (var i = 0; i < chars.Length; i++)
            {
                text.Insert(tx, (uint)i, chars[i]);
            }
        });
    }

    public static Prepared TextAppendOneTxnObserved(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        var chars = Inputs.TypingOf(n);
        long seen = 0;
        var subscription = text.Observe(_ => seen++);
        return new Prepared(
            () =>
            {
                using var tx = doc.WriteTransaction();
                for (var i = 0; i < chars.Length; i++)
                {
                    text.Insert(tx, (uint)i, chars[i]);
                }
            },
            () => $"{seen} {Text(doc, text)}",
            () => StateV1(doc),
            () => StateV2(doc),
            () =>
            {
                subscription.Dispose();
                doc.Dispose();
            });
    }

    public static Prepared EmptyTransactions(Fixtures f)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        return TextResult(doc, text, () =>
        {
            for (var i = 0; i < Inputs.TypingN; i++)
            {
                using var tx = doc.WriteTransaction();
            }
        });
    }

    public static Prepared TextRandom(Fixtures f, int n)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        var edits = Inputs.RandomTyping.AsSpan(0, n).ToArray();
        return TextResult(doc, text, () =>
        {
            foreach (var e in edits)
            {
                using var tx = doc.WriteTransaction();
                text.Insert(tx, (uint)e.Pos, e.Insert);
            }
        });
    }

    private static void Apply(Text text, Transaction tx, Edit e)
    {
        if (e.Delete > 0)
        {
            text.RemoveRange(tx, (uint)e.Pos, (uint)e.Delete);
        }

        if (e.Insert.Length > 0)
        {
            text.Insert(tx, (uint)e.Pos, e.Insert);
        }
    }

    public static Prepared TraceReplay(Fixtures f)
    {
        var trace = f.Trace;
        var doc = NewDoc();
        var text = doc.Text("text");
        return TextResult(doc, text, () =>
        {
            for (var t = 0; t < trace.Transactions; t++)
            {
                using var tx = doc.WriteTransaction();
                for (var i = trace.TxnStarts[t]; i < trace.TxnStarts[t + 1]; i++)
                {
                    Apply(text, tx, trace.Edits[i]);
                }
            }
        });
    }

    public static Prepared Concurrent(Fixtures f)
    {
        byte[] baseUpdate;
        using (var baseDoc = NewDoc(100))
        {
            var baseText = baseDoc.Text("text");
            using (var tx = baseDoc.WriteTransaction())
            {
                baseText.Insert(tx, 0, Inputs.BaseText);
            }

            baseUpdate = StateV1(baseDoc);
        }

        var clients = new Doc[Inputs.Clients];
        var texts = new Text[Inputs.Clients];
        for (var c = 0; c < clients.Length; c++)
        {
            clients[c] = NewDoc(c + 1);
            texts[c] = clients[c].Text("text");
            using var tx = clients[c].WriteTransaction();
            Check(tx.ApplyV1(baseUpdate));
        }

        byte[] baseVector;
        using (var tx = clients[0].ReadTransaction())
        {
            baseVector = tx.StateVectorV1();
        }

        for (var c = 0; c < clients.Length; c++)
        {
            foreach (var e in Inputs.ClientEdits[c])
            {
                using var tx = clients[c].WriteTransaction();
                Apply(texts[c], tx, e);
            }
        }

        return new Prepared(
            () =>
            {
                var diffs = new byte[clients.Length][];
                for (var c = 0; c < clients.Length; c++)
                {
                    using var tx = clients[c].ReadTransaction();
                    diffs[c] = tx.StateDiffV1(baseVector);
                }

                for (var i = 0; i < clients.Length; i++)
                {
                    for (var j = 0; j < clients.Length; j++)
                    {
                        if (i != j)
                        {
                            using var tx = clients[i].WriteTransaction();
                            Check(tx.ApplyV1(diffs[j]));
                        }
                    }
                }
            },
            () =>
            {
                var first = Text(clients[0], texts[0]);
                for (var c = 1; c < clients.Length; c++)
                {
                    if (Text(clients[c], texts[c]) != first)
                    {
                        throw new InvalidOperationException("the clients did not converge");
                    }
                }

                return first;
            },
            () => StateV1(clients[0]),
            () => StateV2(clients[0]),
            () =>
            {
                foreach (var c in clients)
                {
                    c.Dispose();
                }
            });
    }

    private static void Check(TransactionUpdateResult result)
    {
        if (result != TransactionUpdateResult.Ok)
        {
            throw new InvalidOperationException($"apply failed: {result}");
        }
    }

    public static Prepared ApplyState(Fixtures f, string file, bool v2)
    {
        var update = f.Bytes(file);
        Doc last = null;
        Text lastText = null;

        // The previous document is freed in the loop, as ycs' becomes garbage there.
        return new Prepared(
            () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    last?.Dispose();
                    last = new Doc();
                    lastText = last.Text("text");
                    using var tx = last.WriteTransaction();
                    Check(v2 ? tx.ApplyV2(update) : tx.ApplyV1(update));
                }
            },
            () => Text(last, lastText),
            () => StateV1(last),
            () => StateV2(last),
            () => last?.Dispose());
    }

    public static Prepared ApplyUpdates(Fixtures f, string file, bool oneTxn)
    {
        var updates = f.Updates(file);
        var doc = new Doc();
        var text = doc.Text("text");
        return TextResult(doc, text, oneTxn
            ? () =>
            {
                using var tx = doc.WriteTransaction();
                foreach (var u in updates)
                {
                    Check(tx.ApplyV1(u));
                }
            }
            : () =>
            {
                foreach (var u in updates)
                {
                    using var tx = doc.WriteTransaction();
                    Check(tx.ApplyV1(u));
                }
            });
    }

    private static (Doc Doc, Text Text) Loaded(Fixtures f)
    {
        var doc = new Doc();
        var text = doc.Text("text");
        using var tx = doc.WriteTransaction();
        Check(tx.ApplyV1(f.Bytes("trace.yrs.v1")));
        return (doc, text);
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
                    using var tx = doc.ReadTransaction();
                    sink += tx.StateDiffV1(null).Length;
                }
            },
            "v2" => () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    using var tx = doc.ReadTransaction();
                    sink += tx.StateDiffV2(null).Length;
                }
            },
            "sv" => () =>
            {
                for (var i = 0; i < 1000; i++)
                {
                    using var tx = doc.ReadTransaction();
                    sink += tx.StateVectorV1().Length;
                }
            },
            "diff" => () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    using var tx = doc.ReadTransaction();
                    sink += tx.StateDiffV1(halfVector).Length;
                }
            },
            _ => throw new ArgumentException(what),
        };
        return TextResult(doc, text, body);
    }

    /// <summary>An integer as either library may have written it: yrs reads a Yjs number as a double.</summary>
    private static long Number(Output o) => o.Tag == OutputTag.Double ? (long)o.Double : o.Long;

    public static string MapContent(Doc doc, Map map)
    {
        using var tx = doc.ReadTransaction();
        using var it = map.Iterate(tx);
        return Canonical.Map([.. it.Select(kv => (kv.Key, Number(kv.Value)))]);
    }

    public static string ArrayContent(Doc doc, YArray array)
    {
        using var tx = doc.ReadTransaction();
        using var it = array.Iterate(tx);
        return Canonical.Array([.. it.Select(Number)]);
    }

    private static Prepared MapResult(Doc doc, Map map, Action body) =>
        new(body, () => MapContent(doc, map), () => StateV1(doc), () => StateV2(doc), doc.Dispose);

    public static Prepared MapSetDistinct(Fixtures f, bool oneTxn)
    {
        var doc = NewDoc();
        var map = doc.Map("map");
        var keys = Inputs.Keys;
        return MapResult(doc, map, oneTxn
            ? () =>
            {
                using var tx = doc.WriteTransaction();
                for (var i = 0; i < keys.Length; i++)
                {
                    using var input = Input.Double(i);
                    map.Insert(tx, keys[i], input);
                }
            }
            : () =>
            {
                for (var i = 0; i < keys.Length; i++)
                {
                    using var tx = doc.WriteTransaction();
                    using var input = Input.Double(i);
                    map.Insert(tx, keys[i], input);
                }
            });
    }

    public static Prepared MapSetSame(Fixtures f)
    {
        var doc = NewDoc();
        var map = doc.Map("map");
        return MapResult(doc, map, () =>
        {
            for (var i = 0; i < Inputs.MapN; i++)
            {
                using var tx = doc.WriteTransaction();
                using var input = Input.Double(i);
                map.Insert(tx, "key", input);
            }
        });
    }

    private static Prepared ArrayResult(Doc doc, YArray array, Action body) =>
        new(body, () => ArrayContent(doc, array), () => StateV1(doc), () => StateV2(doc), doc.Dispose);

    public static Prepared ArrayPush(Fixtures f, bool oneTxn, int n)
    {
        var doc = NewDoc();
        var array = doc.Array("array");
        return ArrayResult(doc, array, oneTxn
            ? () =>
            {
                using var tx = doc.WriteTransaction();
                for (var i = 0; i < n; i++)
                {
                    using var input = Input.Double(i);
                    array.InsertRange(tx, (uint)i, input);
                }
            }
            : () =>
            {
                for (var i = 0; i < n; i++)
                {
                    using var tx = doc.WriteTransaction();
                    using var input = Input.Double(i);
                    array.InsertRange(tx, (uint)i, input);
                }
            });
    }

    public static Prepared ArrayInsertRandom(Fixtures f)
    {
        var doc = NewDoc();
        var array = doc.Array("array");
        var positions = Inputs.ArrayInserts;
        return ArrayResult(doc, array, () =>
        {
            for (var i = 0; i < positions.Length; i++)
            {
                using var tx = doc.WriteTransaction();
                using var input = Input.Double(i);
                array.InsertRange(tx, (uint)positions[i], input);
            }
        });
    }

    /// <summary>One call with every value: one item, on both libraries.</summary>
    private static YArray FilledArray(Doc doc)
    {
        var array = doc.Array("array");
        var inputs = Enumerable.Range(0, Inputs.ArrayN).Select(i => Input.Double(i)).ToArray();
        using (var tx = doc.WriteTransaction())
        {
            array.InsertRange(tx, 0, inputs);
        }

        foreach (var input in inputs)
        {
            input.Dispose();
        }

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
                using var tx = doc.WriteTransaction();
                array.RemoveRange(tx, (uint)p, 1);
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
                    using var tx = doc.ReadTransaction();
                    chars += text.String(tx).Length;
                }
            },
            () => $"{chars} {Text(doc, text)}",
            () => StateV1(doc),
            () => StateV2(doc),
            doc.Dispose);
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
                    using var tx = doc.ReadTransaction();
                    using var it = array.Iterate(tx);
                    foreach (var o in it)
                    {
                        sum += (long)o.Double;
                    }
                }
            },
            () => sum.ToString(),
            () => StateV1(doc),
            () => StateV2(doc),
            doc.Dispose);
    }

    public static Prepared ReadMap(Fixtures f)
    {
        var doc = NewDoc();
        var map = doc.Map("map");
        using (var tx = doc.WriteTransaction())
        {
            for (var i = 0; i < Inputs.MapN; i++)
            {
                using var input = Input.Double(i);
                map.Insert(tx, Inputs.Keys[i], input);
            }
        }

        long sum = 0;
        return new Prepared(
            () =>
            {
                for (var r = 0; r < 10; r++)
                {
                    using var tx = doc.ReadTransaction();
                    using var it = map.Iterate(tx);
                    foreach (var kv in it)
                    {
                        sum += kv.Key.Length + (long)kv.Value.Double;
                    }
                }
            },
            () => sum.ToString(),
            () => StateV1(doc),
            () => StateV2(doc),
            doc.Dispose);
    }

    public static Prepared Undo(Fixtures f, string phase)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        var undo = new YUndoManager(doc, text, new YUndoManagerOptions { CaptureTimeoutMilliseconds = 0 });
        var chars = Inputs.Typing.AsSpan(0, Inputs.UndoN).ToArray();
        void Type()
        {
            for (var i = 0; i < chars.Length; i++)
            {
                using var tx = doc.WriteTransaction();
                text.Insert(tx, (uint)i, chars[i]);
            }
        }

        var steps = 0;
        void UndoAll()
        {
            while (undo.Undo())
            {
                steps++;
            }
        }

        void RedoAll()
        {
            while (undo.Redo())
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
        return new Prepared(
            body,
            () => $"{(phase == "type" ? chars.Length : steps)} {Text(doc, text)}",
            () => StateV1(doc),
            () => StateV2(doc),
            () =>
            {
                undo.Dispose();
                doc.Dispose();
            });
    }

    public static Prepared Observe(Fixtures f, bool readDelta)
    {
        var doc = NewDoc();
        var text = doc.Text("text");
        var chars = Inputs.Typing;
        long seen = 0;
        var subscription = text.Observe(e => seen += readDelta ? e.Delta.Count : 1);
        return new Prepared(
            () =>
            {
                for (var i = 0; i < chars.Length; i++)
                {
                    using var tx = doc.WriteTransaction();
                    text.Insert(tx, (uint)i, chars[i]);
                }
            },
            () => $"{seen} {Text(doc, text)}",
            () => StateV1(doc),
            () => StateV2(doc),
            () =>
            {
                subscription.Dispose();
                doc.Dispose();
            });
    }

    public static (string Text, byte[] V1, byte[] V2, List<byte[]> Updates, byte[] HalfVector) Record(Trace trace)
    {
        using var doc = NewDoc();
        var text = doc.Text("text");
        var updates = new List<byte[]>(trace.Transactions);
        using var subscription = doc.ObserveUpdatesV1(e => updates.Add(e.Update));
        byte[] half = null;
        for (var t = 0; t < trace.Transactions; t++)
        {
            if (t == trace.Transactions / 2)
            {
                using var read = doc.ReadTransaction();
                half = read.StateVectorV1();
            }

            using var tx = doc.WriteTransaction();
            for (var i = trace.TxnStarts[t]; i < trace.TxnStarts[t + 1]; i++)
            {
                Apply(text, tx, trace.Edits[i]);
            }
        }

        return (Text(doc, text), StateV1(doc), StateV2(doc), updates, half);
    }

    public static string ContentOf(byte[] v1, string root)
    {
        using var doc = new Doc();
        var text = doc.Text("text");
        var map = doc.Map("map");
        var array = doc.Array("array");
        using (var tx = doc.WriteTransaction())
        {
            Check(tx.ApplyV1(v1));
        }

        return root switch
        {
            "text" => Text(doc, text),
            "map" => MapContent(doc, map),
            "array" => ArrayContent(doc, array),
            _ => throw new ArgumentException(root),
        };
    }
}
