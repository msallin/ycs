using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ycs.Benchmarks;

/// <summary>
/// Operations whose cost grew with the size of what they touch (the second review's P1 to P4), at
/// the sizes the review measured. Each case runs once on a fresh document and reports its time and
/// what it allocated. The Yjs timings of the same cases are in the review (docs/review-r2.md).
/// </summary>
internal static class Perf
{
    public static void Run()
    {
        // P1: a character deleted in front of a deleted run merges into it.
        Case("P1 delete 40,000 characters backwards, UndoManager attached", () =>
        {
            var (doc, text) = Typed(40_000);
            _ = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = int.MaxValue });
            return () =>
            {
                for (var i = 40_000 - 1; i >= 0; i--)
                {
                    text.Delete(i, 1);
                }
            };
        });

        Case("P1 undo 40,000 keystrokes", () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var text = doc.GetText("t");
            var undoManager = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
            for (var i = 0; i < 40_000; i++)
            {
                text.Insert(i, "a");
            }

            return () =>
            {
                for (var i = 0; i < 40_000; i++)
                {
                    undoManager.Undo();
                }
            };
        });

        Case("redo 40,000 keystrokes", () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var text = doc.GetText("t");
            var undoManager = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
            for (var i = 0; i < 40_000; i++)
            {
                text.Insert(i, "a");
            }

            for (var i = 0; i < 40_000; i++)
            {
                undoManager.Undo();
            }

            return () =>
            {
                for (var i = 0; i < 40_000; i++)
                {
                    undoManager.Redo();
                }
            };
        });

        // P2: every transaction merged into the stack item rebuilt both of its delete sets.
        Case("P2 40,000 edits at random places in one capture", () =>
        {
            var (doc, text) = Typed(40_000);
            _ = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = int.MaxValue });
            var random = new Random(1);
            return () =>
            {
                for (var i = 0; i < 40_000; i++)
                {
                    text.Insert(random.Next(text.Length + 1), "bb");
                    text.Delete(random.Next(text.Length), 1);
                }
            };
        });

        // P3: the deep observer's events sorted by path length.
        Case("P3 one transaction changes 16,000 maps under a deep observer", () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("a");
            var maps = new List<YMap>();
            doc.Transact(_ =>
            {
                for (var i = 0; i < 16_000; i++)
                {
                    var map = new YMap();
                    maps.Add(map);
                    array.Add(new object[] { map });
                }
            });
            var events = 0;
            array.DeepEventHandler += (_, e) => events += e.Events.Count;
            return () => doc.Transact(_ =>
            {
                foreach (var map in maps)
                {
                    map.Set("k", 1);
                }
            });
        });

        // P4: Get(i) over a legacy ContentJSON item.
        Case("P4 Get(i) for every i of a ContentJSON item of 80,000 values", () =>
        {
            var doc = new YDoc();
            doc.ApplyUpdate(ContentJsonUpdate(80_000), null, UpdateLimits.Unlimited);
            var array = doc.GetArray("a");
            return () =>
            {
                for (var i = 0; i < array.Length; i++)
                {
                    _ = array.Get(i);
                }
            };
        });
    }

    private static (YDoc Doc, YText Text) Typed(int length)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        for (var i = 0; i < length; i++)
        {
            text.Insert(i, "a");
        }

        return (doc, text);
    }

    /// <summary>A v1 update: one ContentJSON item (content 2) of <paramref name="count"/> values "1" in the root array "a".</summary>
    private static byte[] ContentJsonUpdate(int count)
    {
        var bytes = new List<byte> { 1, 1, 9, 0, 2, 1, 1, (byte)'a' };
        for (var v = (uint)count; ; v >>= 7)
        {
            bytes.Add((byte)(v > 0x7F ? 0x80 | (v & 0x7F) : v));
            if (v <= 0x7F)
            {
                break;
            }
        }

        for (var i = 0; i < count; i++)
        {
            bytes.Add(1);
            bytes.Add((byte)'1');
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void Case(string name, Func<Action> setup)
    {
        var run = setup();
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        var a0 = System.GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        run();
        sw.Stop();
        var allocated = System.GC.GetTotalAllocatedBytes(true) - a0;
        Console.WriteLine($"{name,-66} {sw.ElapsedMilliseconds,7} ms  {allocated / 1048576.0,9:F1} MiB");
    }
}
