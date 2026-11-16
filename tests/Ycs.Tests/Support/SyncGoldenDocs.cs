using System.Collections.Generic;

using System;
using System.Security.Cryptography;

namespace Ycs;

/// <summary>The documents whose sync messages Fixtures/sync-messages.json pins, built the same way every time.</summary>
internal static class SyncGoldenDocs
{
    public static IEnumerable<(string Name, YDoc Doc)> All()
    {
        yield return ("empty", new YDoc(new YDocOptions { ClientId = 1 }));

        var text = new YDoc(new YDocOptions { ClientId = 2 });
        var t = text.GetText("t");
        t.Insert(0, "hello world");
        t.Remove(2, 3);
        t.Format(0, 2, new Dictionary<string, YValue> { ["bold"] = true });
        yield return ("text", text);

        var mixed = new YDoc(new YDocOptions { ClientId = 3 });
        mixed.GetMap("m")["a"] = 1.5;
        mixed.GetMap("m")["b"] = "two";
        mixed.GetMap("m").Remove("a");
        mixed.GetArray("a").Add("x", 2, true);
        mixed.GetArray("a").RemoveAt(1);
        yield return ("mixed", mixed);

        var merged = new YDoc(new YDocOptions { ClientId = 4 });
        merged.ApplyUpdate(text.EncodeStateAsUpdate());
        merged.GetText("t").Insert(0, new string('é', 300));
        yield return ("merged", merged);

        var large = new YDoc(new YDocOptions { ClientId = 5 });
        large.GetText("big").Insert(0, new string('z', 17000));
        yield return ("large", large);
    }

    /// <summary>The bytes as hex, or for a long message its SHA-256, so the fixture stays small.</summary>
    public static string Describe(ReadOnlySpan<byte> bytes) =>
        bytes.Length <= 600 ? Convert.ToHexString(bytes) : "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
}
