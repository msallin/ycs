namespace Ycs.Budgets;

/// <summary>One measured operation: <see cref="Setup"/> builds the state and returns the operation, called with 0, 1, 2, ...</summary>
internal sealed record Scenario(string Name, int Iterations, Func<Action<int>> Setup);

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

        new("doc.apply-remote-char-update-100k", 500, () =>
        {
            var (doc, _) = LargeText();
            var remote = new YDoc(new YDocOptions { ClientId = 2 });
            remote.ApplyUpdateV1(doc.EncodeStateAsUpdateV1());
            var updates = new List<byte[]>();
            remote.Update += (_, e) => updates.Add(e.data);
            var remoteText = remote.GetText("text");
            for (var i = 0; i < 500 + Warmup; i++)
            {
                remoteText.Insert(TextLength / 3 + i, "y");
            }

            return i => doc.ApplyUpdateV1(updates[i]);
        }),

        new("doc.encode-small-document-v1", 500, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            doc.GetText("text").Insert(0, "hello world");
            var map = doc.GetMap("map");
            map.Set("a", 1);
            map.Set("b", "two");
            map.Set("c", true);
            return _ => doc.EncodeStateAsUpdateV1();
        }),

        new("doc.encode-diff-of-one-edit-100k", 500, () =>
        {
            var (doc, text) = LargeText();
            var stateVector = doc.EncodeStateVector();
            text.Insert(TextLength / 2, "z");
            return _ => doc.EncodeStateAsUpdateV1(stateVector);
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
                doc.EncodeStateAsUpdateV1(destination, stateVector);
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
            return i => RelativePosition.FromTypeIndex(text, 90_000 + (i % 1000));
        }),

        new("position.absolute-from-relative-deep-100k", 500, () =>
        {
            var (doc, text) = LargeText();
            var positions = Enumerable.Range(0, 1000).Select(i => RelativePosition.FromTypeIndex(text, 90_000 + i)).ToArray();
            return i => AbsolutePosition.CreateAbsolutePositionFromRelativePosition(positions[i % positions.Length], doc);
        }),

        new("map.set-existing-key", 500, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var map = doc.GetMap("map");
            var keys = Enumerable.Range(0, 100).Select(i => $"key{i}").ToArray();
            foreach (var key in keys)
            {
                map.Set(key, 0);
            }

            return i => map.Set(keys[i % keys.Length], i);
        }),
    ];

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
                text.Delete(random.Next(text.Length - 5), 1 + random.Next(4));
            }
        }

        text.Delete(TextLength, text.Length - TextLength);
        return (doc, text);
    }
}
