namespace Ycs.Budgets;

/// <summary>
/// One measured operation: <see cref="Setup"/> builds the state and returns the operation, called with 0, 1, 2, ...;
/// <see cref="Warmups"/> calls before the measured ones (null: <see cref="Scenarios.WarmupIterations"/>).
/// </summary>
internal sealed record Scenario(string Name, int Iterations, Func<Action<int>> Setup, int? Warmups = null);

/// <summary>The fixed scenario set. Adding one means adding its budget to <see cref="Budgets"/>.</summary>
internal static class Scenarios
{
    private const int TextLength = 100_000;
    private const int Warmup = 20;

    public static int WarmupIterations => Warmup;

    public static readonly IReadOnlyList<Scenario> All =
    [
        new("text.insert-char-at-caret-100k", 500, () =>
        {
            var (_, text) = LargeText();
            var caret = TextLength / 2;
            return _ => text.Insert(caret++, "x");
        }),

        new("text.insert-char-in-document-of-5000-clients", 500, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var text = doc.GetText("text");
            text.Insert(0, "base");
            var state = doc.EncodeStateAsUpdate();
            for (var c = 0; c < 5000; c++)
            {
                var peer = new YDoc(new YDocOptions { ClientId = 1000 + c });
                peer.ApplyUpdate(state);
                peer.GetText("text").Insert(0, "x");
                doc.ApplyUpdate(peer.EncodeStateAsUpdate(doc.EncodeStateVector()));
            }

            return i => text.Insert(text.Length, "x");
        }),

        new("doc.apply-remote-char-update-100k", 500, () =>
        {
            var (doc, _) = LargeText();
            var remote = new YDoc(new YDocOptions { ClientId = 2 });
            remote.ApplyUpdate(doc.EncodeStateAsUpdate());
            var updates = new List<byte[]>();
            remote.Updated += (_, e) => updates.Add(e.Update.ToArray());
            var remoteText = remote.GetText("text");
            for (var i = 0; i < 500 + Warmup; i++)
            {
                remoteText.Insert(TextLength / 3 + i, "y");
            }

            return i => doc.ApplyUpdate(updates[i]);
        }),

        new("doc.encode-small-document-v1", 500, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            doc.GetText("text").Insert(0, "hello world");
            var map = doc.GetMap("map");
            map["a"] = 1;
            map["b"] = "two";
            map["c"] = true;
            return _ => doc.EncodeStateAsUpdate();
        }),

        new("doc.encode-diff-of-one-edit-100k", 500, () =>
        {
            var (doc, text) = LargeText();
            var stateVector = doc.EncodeStateVector();
            text.Insert(TextLength / 2, "z");
            return _ => doc.EncodeStateAsUpdate(stateVector);
        }),

        new("doc.encode-diff-100k-into-buffer-writer", 500, () =>
        {
            var (doc, text) = LargeText();
            var stateVector = doc.EncodeStateVector();
            text.Insert(TextLength / 2, "z");
            var destination = new System.Buffers.ArrayBufferWriter<byte>(1 << 16);
            return _ =>
            {
                destination.ResetWrittenCount();
                doc.EncodeStateAsUpdate(destination, stateVector);
            };
        }),

        new("text.to-string-100k", 100, () =>
        {
            var (_, text) = LargeText();
            return _ => text.ToString();
        }),

        new("text.to-string-after-edit-100k", 100, () =>
        {
            // ToString keeps its result until the text changes; this builds it again each time.
            var (_, text) = LargeText();
            var caret = TextLength / 2;
            return _ =>
            {
                text.Insert(caret++, "x");
                text.ToString();
            };
        }),

        new("position.relative-from-index-deep-100k", 500, () =>
        {
            var (_, text) = LargeText();
            return i => RelativePosition.FromIndex(text, 90_000 + (i % 1000));
        }),

        new("position.absolute-from-relative-deep-100k", 500, () =>
        {
            var (doc, text) = LargeText();
            var positions = Enumerable.Range(0, 1000).Select(i => RelativePosition.FromIndex(text, 90_000 + i)).ToArray();
            return i => positions[i % positions.Length].TryResolve(doc, out _);
        }),

        new("map.set-existing-key", 500, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var map = doc.GetMap("map");
            var keys = Enumerable.Range(0, 100).Select(i => $"key{i}").ToArray();
            foreach (var key in keys)
            {
                map[key] = 0;
            }

            return i => map[keys[i % keys.Length]] = i;
        }),

        new("text.insert-formatted-char-100k", 500, () =>
        {
            var (_, text) = LargeText();
            YAttributes bold = [new("bold", true)];
            var caret = TextLength / 2;
            return _ => text.Insert(caret++, "x", bold);
        }),

        new("text.event-delta-read-100k", 500, () =>
        {
            // An editor binding: every keystroke's observer reads the delta.
            var (_, text) = LargeText();
            var ops = 0;
            text.Observe(e => ops += e.Delta.Count);
            var caret = TextLength / 2;
            return _ => text.Insert(caret++, "x");
        }),

        new("undo.undo-redo-typing-100k", 500, () =>
        {
            var (doc, text) = LargeText();
            var undoManager = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
            for (var i = 0; i < 1000; i++)
            {
                text.Insert(TextLength / 2 + i, "x");
            }

            return _ =>
            {
                undoManager.Undo();
                undoManager.Redo();
            };
        }),

        new("undo.redo-after-undoing-typing-100k", 500, () =>
        {
            // Every redo splits the undone run (merged while deleted) at its front.
            var (_, text) = LargeText();
            var undoManager = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
            const int typed = 20_000;
            for (var i = 0; i < typed; i++)
            {
                text.Insert(TextLength / 2 + i, "x");
            }

            for (var i = 0; i < typed; i++)
            {
                undoManager.Undo();
            }

            return _ => undoManager.Redo();
        }),

        new("undo.delete-backwards-one-capture-100k", 500, () =>
        {
            // Backspace with an UndoManager whose capture never ends: the deleted characters are kept.
            var (_, text) = LargeText();
            _ = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.MaxValue });
            var caret = TextLength / 2;
            return _ => text.Remove(--caret, 1);
        }),

        new("array.push-100k-in-one-transaction", 3, () =>
        {
            // One operation is the whole transaction, on a fresh document: the pushes and the merge at its end.
            YValue value = 1L;
            return _ =>
            {
                var doc = new YDoc(new YDocOptions { ClientId = 1 });
                var array = doc.GetArray("array");
                doc.Transact(_ =>
                {
                    for (var i = 0; i < 100_000; i++)
                    {
                        array.Add(value);
                    }
                });
            };
        }, Warmups: 2),

        new("array.enumerate-100k", 20, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("array");
            YValue[] values = new YValue[100_000];
            Array.Fill(values, (YValue)1L);
            doc.Transact(_ => array.Add(values));
            return _ =>
            {
                long sum = 0;
                foreach (var value in array)
                {
                    sum += (long)value;
                }

                GC.KeepAlive(sum);
            };
        }),

        new("text.type-100k-in-one-transaction", 3, () =>
        {
            // One operation is the whole transaction on a fresh document: 100,000 keystrokes at the end, merged at its end.
            return _ =>
            {
                var doc = new YDoc(new YDocOptions { ClientId = 1 });
                var text = doc.GetText("text");
                doc.Transact(_ =>
                {
                    for (var i = 0; i < 100_000; i++)
                    {
                        text.Insert(i, "x");
                    }
                });
            };
        }, Warmups: 2),

        new("array.delete-random-100k", 5000, () =>
        {
            // Single deletes at random places of one 100,000-element run: the pieces it is split into continue each
            // other by clock, which a marker lookup must not walk across.
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("array");
            array.Add(Enumerable.Range(0, 100_000).Select(i => (object)(long)i).ToList().Select(YValue.FromObject).ToArray());
            var random = new Random(7);
            return _ => array.RemoveRange(random.Next(array.Count), 1);
        }),

        new("doc.encode-state-with-pending-100k", 100, () =>
        {
            var (doc, _) = LargeText();
            var other = new YDoc(new YDocOptions { ClientId = 3 });
            var otherText = other.GetText("text");
            otherText.Insert(0, "a");
            otherText.Insert(1, "b");

            // "b" without the "a" before it (the state vector { 3: 1 }): it waits.
            doc.ApplyUpdate(other.EncodeStateAsUpdate(new byte[] { 1, 3, 1 }));
            return _ => doc.EncodeStateAsUpdate();
        }),

        .. UpdateScenarios(UpdateFormat.V1, "v1"),
        .. UpdateScenarios(UpdateFormat.V2, "v2"),
    ];

    /// <summary>
    /// The operations on encoded updates, which read and write them without a document: merge of ten large updates and
    /// of two states, diff, convert, the state vector and the metadata of an update of a 100,000-character text.
    /// </summary>
    private static IEnumerable<Scenario> UpdateScenarios(UpdateFormat format, string tag)
    {
        var other = format == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;
        yield return new($"update.merge-10-large-{tag}", 30, () =>
        {
            var inputs = Enumerable.Range(0, 10)
                .Select(c => (ReadOnlyMemory<byte>)InFormat(TypedDoc(10 + c, 8000, 100 + c).EncodeStateAsUpdate(), format))
                .ToArray();
            return _ => YUpdate.Merge(inputs, format);
        });

        yield return new($"update.merge-two-states-100k-20k-{tag}", 30, () =>
        {
            var inputs = new[]
            {
                (ReadOnlyMemory<byte>)InFormat(TypedDoc(1, TextLength, 42).EncodeStateAsUpdate(), format),
                InFormat(TypedDoc(2, 20_000, 9).EncodeStateAsUpdate(), format),
            };
            return _ => YUpdate.Merge(inputs, format);
        });

        yield return new($"update.diff-100k-{tag}", 30, () =>
        {
            var state = InFormat(TypedDoc(1, TextLength, 42).EncodeStateAsUpdate(), format);
            byte[] stateVector = [1, 1, 100];
            return _ => YUpdate.Diff(state, stateVector, format);
        });

        yield return new($"update.convert-100k-{tag}", 30, () =>
        {
            var state = InFormat(TypedDoc(1, TextLength, 42).EncodeStateAsUpdate(), format);
            return _ => YUpdate.Convert(state, format, other);
        });

        yield return new($"update.encode-state-vector-100k-{tag}", 30, () =>
        {
            var state = InFormat(TypedDoc(1, TextLength, 42).EncodeStateAsUpdate(), format);
            return _ => YUpdate.EncodeStateVector(state, format);
        });

        yield return new($"update.parse-meta-100k-{tag}", 30, () =>
        {
            var state = InFormat(TypedDoc(1, TextLength, 42).EncodeStateAsUpdate(), format);
            return _ => YUpdate.ParseMeta(state, format);
        });
    }

    private static byte[] InFormat(byte[] v1, UpdateFormat format) =>
        format == UpdateFormat.V1 ? v1 : YUpdate.Convert(v1, UpdateFormat.V1, format);

    private static YDoc TypedDoc(int clientId, int length, int seed)
    {
        var doc = new YDoc(new YDocOptions { ClientId = clientId });
        var text = doc.GetText("t");
        var random = new Random(seed);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz ,.";
        while (text.Length < length)
        {
            var run = new string(Enumerable.Range(0, 1 + random.Next(16)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            text.Insert(random.Next(text.Length + 1), run);
            if (random.Next(8) == 0 && text.Length > 10)
            {
                text.Remove(random.Next(text.Length - 5), 1 + random.Next(4));
            }
        }

        return doc;
    }

    /// <summary>
    /// A 100 000-character text typed the way people type: short runs at scattered positions, some
    /// deleted again, so it is made of many items (one big insert would be a single item).
    /// </summary>
    private static (YDoc Doc, YText Text) LargeText()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("text");
        var random = new Random(42);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz ,.";
        while (text.Length < TextLength)
        {
            var run = new string(Enumerable.Range(0, 1 + random.Next(16)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            text.Insert(random.Next(text.Length + 1), run);
            if (random.Next(8) == 0 && text.Length > 10)
            {
                text.Remove(random.Next(text.Length - 5), 1 + random.Next(4));
            }
        }

        text.Remove(TextLength, text.Length - TextLength);
        return (doc, text);
    }
}
