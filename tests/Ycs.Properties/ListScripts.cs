using static Ycs.Properties.Json;

namespace Ycs.Properties;

/// <summary>
/// Scripts that exercise search markers and the merge at the end of a transaction: a text and an array grown to a
/// few hundred elements by typing runs, backspacing, random inserts and deletes and pushes, many of them in one
/// transaction (where a run of new items is merged at the end), with undo, a format now and then (it turns a text's
/// markers off) and syncs between two clients. The general generator's documents stay too short to make markers.
/// An UndoManager comes late if at all, on either client (it observes the document, so its client extends no runs).
/// With <paramref name="windows"/>, updates of the other client are applied in the middle of transactions, where
/// they leave the search markers stale, as in Yjs.
/// </summary>
internal static class ListScripts
{
    private static readonly List<object> Text = ["t"];
    private static readonly List<object> Array = ["a"];

    public static Dictionary<string, object> Generate(ulong seed, bool windows = false)
    {
        var rng = new Rng(seed ^ 0x6d61726b657273UL);
        var clients = new List<object>
        {
            Obj(("id", new JsonNumber("1")), ("gc", rng.Chance(0.6))),
            Obj(("id", new JsonNumber("2")), ("gc", rng.Chance(0.6))),
        };
        var script = Obj(
            ("clients", clients),
            ("roots", new List<object> { Obj(("name", "t"), ("kind", "text")), Obj(("name", "a"), ("kind", "array")), Obj(("name", "m"), ("kind", "map")) }),
            ("steps", new List<object>()));
        var steps = (List<object>)script["steps"];
        var run = new ScriptRun((Dictionary<string, object>)Parse(Canonical(script)));
        var undo = new List<(int Client, string Id)>();

        void Emit(Dictionary<string, object> step)
        {
            var normalized = (Dictionary<string, object>)Parse(Canonical(step));
            steps.Add(normalized);
            run.Step(normalized);
        }

        int LengthOf(int c, List<object> path) => run.Targets(c).FirstOrDefault(t => (string)t.Path[0] == (string)path[0])?.Length ?? 0;

        List<object> Ops(int c, int count)
        {
            var ops = new List<object>();
            var text = LengthOf(c, Text);
            var array = LengthOf(c, Array);
            var caret = rng.Int(text + 1);
            var slot = rng.Int(array + 1);
            for (var i = 0; i < count; i++)
            {
                var r = rng.Int(100);
                if (r < 30)
                {
                    // Typing: the next character after the last one.
                    ops.Add(Obj(("op", "insert"), ("path", Text), ("at", (long)caret), ("text", rng.Chance(0.9) ? "x" : "yz")));
                    caret += 1;
                    text += 1;
                }
                else if (r < 40 && caret > 0)
                {
                    // Backspace.
                    caret--;
                    ops.Add(Obj(("op", "delete"), ("path", Text), ("at", (long)caret), ("len", 1L)));
                    text--;
                }
                else if (r < 50)
                {
                    caret = rng.Int(text + 1);
                    ops.Add(Obj(("op", "insert"), ("path", Text), ("at", (long)caret), ("text", rng.Chance(0.5) ? "ab" : "q")));
                    text += 1;
                }
                else if (r < 58 && text > 0)
                {
                    var at = rng.Int(text);
                    var len = rng.Range(1, Math.Min(4, text - at));
                    ops.Add(Obj(("op", "delete"), ("path", Text), ("at", (long)at), ("len", (long)len)));
                    text -= len;
                    caret = at;
                }
                else if (r < 72)
                {
                    ops.Add(Obj(("op", "push"), ("path", Array), ("values", new List<object> { (long)rng.Int(1000) })));
                    array++;
                }
                else if (r < 82)
                {
                    ops.Add(Obj(("op", "ains"), ("path", Array), ("at", (long)slot), ("values", new List<object> { (long)rng.Int(1000), "s" })));
                    slot += 2;
                    array += 2;
                }
                else if (r < 92 && array > 0)
                {
                    var at = rng.Int(array);
                    var len = rng.Range(1, Math.Min(3, array - at));
                    ops.Add(Obj(("op", "delete"), ("path", Array), ("at", (long)at), ("len", (long)len)));
                    array -= len;
                    slot = Math.Min(slot, array);
                }
                else if (r < 98)
                {
                    ops.Add(Obj(("op", "set"), ("path", new List<object> { "m" }), ("key", rng.Chance(0.5) ? "k" : "j"), ("value", (long)rng.Int(10))));
                }
                else if (text > 0)
                {
                    var at = rng.Int(text);
                    ops.Add(Obj(("op", "format"), ("path", Text), ("at", (long)at), ("len", (long)rng.Range(1, Math.Min(3, text - at))), ("attrs", Obj(("bold", true)))));
                }
            }

            return ops;
        }

        // A long start, so that markers are made and moved.
        Emit(Obj(("op", "insert"), ("c", 0L), ("path", Text), ("at", 0L), ("text", new string('s', rng.Range(50, 300)))));
        var stepCount = rng.Range(15, 40);
        for (var s = 0; s < stepCount; s++)
        {
            var c = rng.Int(2);
            var r = rng.Int(100);
            if (r < 45)
            {
                var ops = Ops(c, rng.Range(5, 40));
                if (windows && run.LogCount(1 - c) > 0 && rng.Chance(0.4))
                {
                    var k = run.LogCount(1 - c) - 1 - rng.Int(Math.Min(3, run.LogCount(1 - c)));
                    ops.Insert(rng.Int(ops.Count + 1), Obj(("op", "deliver"), ("from", (long)(1 - c)), ("k", (long)k), ("to", (long)c), ("via", rng.Chance(0.5) ? "v1" : "v2")));
                }

                Emit(Obj(("op", "transact"), ("c", (long)c), ("origin", null), ("ops", ops)));
            }
            else if (r < 75)
            {
                foreach (Dictionary<string, object> op in Ops(c, 1))
                {
                    op["c"] = (long)c;
                    Emit(op);
                }
            }
            else if (r < 85)
            {
                Emit(Obj(("op", "sync"), ("from", (long)c), ("to", (long)(1 - c)), ("via", rng.Chance(0.5) ? "v1" : "v2")));
            }
            else if (r < 89 && (undo.Count > 0 || s > stepCount / 2))
            {
                var id = "u" + undo.Count;
                undo.Add((c, id));
                Emit(Obj(("op", "um"), ("c", (long)c), ("id", id), ("scope", new List<object> { Text, Array }), ("timeout", rng.Chance(0.5) ? "inf" : "0")));
            }
            else if (undo.Count > 0)
            {
                var (owner, id) = rng.Pick(undo);
                Emit(Obj(("op", rng.Chance(0.6) ? "undo" : rng.Chance(0.7) ? "redo" : "stop"), ("c", (long)owner), ("id", id)));
            }
        }

        return (Dictionary<string, object>)Parse(Canonical(script));
    }
}
