using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="DecodedStruct"/>: the v1 and the v2 encoding of an update give the same records (two decoders, one result),
/// the records hold what was inserted, and the records of damaged updates that still decode are well formed.
/// </summary>
[TestClass]
public class DecodedStructTests
{
    private const int Damaged = 20000;

    [TestMethod]
    public void Decode_CorpusUpdates_V1AndV2GiveTheSameRecords()
    {
        var inputs = CorpusUpdates();

        var structs = CompareFormats(inputs, i => i % 2 == 0 ? UpdateFormat.V1 : UpdateFormat.V2);

        Assert.IsGreaterThan(500, structs);
    }

    [TestMethod]
    public void Decode_EveryContentKind_V1AndV2GiveTheSameRecordsAndTheKindsAreAllSeen()
    {
        var inputs = RichUpdates();

        CompareFormats(inputs, i => i < 4 && i % 2 == 0 ? UpdateFormat.V1 : UpdateFormat.V2);

        var kinds = inputs
            .SelectMany(Decodable)
            .Select(s => s.Kind)
            .Distinct()
            .Order()
            .ToList();
        CollectionAssert.AreEqual(Enum.GetValues<ItemKind>().Where(k => k != ItemKind.Json).Order().ToList(), kinds);
    }

    [TestMethod]
    public void Decode_RichUpdate_RecordsHoldWhatWasInserted()
    {
        var structs = YUpdate.Decode(RichUpdates()[0]).Structs;

        var found = (
            structs.Any(s => s.Kind == ItemKind.String && s.Text!.EndsWith(" world", StringComparison.Ordinal)),
            structs.Any(s => s.Kind == ItemKind.String && s.Text == "h" && s.ParentName == "t"),
            structs.Any(s => s.Kind == ItemKind.Format && s.FormatKey == "bold" && Equals(YValue.FromStored(s.FormatValue), YValue.FromStored(true))),
            structs.Any(s => s.Kind == ItemKind.Doc && s.DocGuid == "sub-1"),
            structs.Any(s => s.Kind == ItemKind.Type && s.TypeName == "div"),
            structs.Any(s => s.Kind == ItemKind.Type && s.TypeName == "hk"),
            structs.Any(s => s.Kind == ItemKind.Any && s.Content.Count == 3 && Equals(s.Content[0], 3.5) && s.Content[1] == null && Equals(s.Content[2], true)),
            structs.Any(s => s.Kind == ItemKind.Binary && s.Content.Count == 1 && ((byte[])s.Content[0]!).SequenceEqual(new byte[] { 1, 2, 3 })));
        Assert.AreEqual((true, true, true, true, true, true, true, true), found);
    }

    [TestMethod]
    public void Decode_DamagedUpdates_EveryDecodedRecordIsWellFormed()
    {
        var seeds = CorpusUpdates().Concat(RichUpdates()).ToList();
        var random = new Random(20261116);
        var inputs = new List<byte[]>();
        while (inputs.Count < Damaged)
        {
            inputs.Add(Damage(seeds[random.Next(seeds.Count)], random));
        }

        var (updates, structs) = (0, 0);
        foreach (var input in inputs)
        {
            foreach (var s in Decodable(input))
            {
                AssertWellFormed(s);
                structs++;
            }

            updates++;
        }

        Assert.AreEqual(Damaged, updates);
        Assert.IsGreaterThan(1500, structs);
    }

    [TestMethod]
    public void Detach_AGapInTheClocks_IsASkipOfTheGapsLength()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        doc.UpdatedV2 += (_, e) => updates.Add(e.Update.ToArray());
        for (var i = 0; i < 4; i++)
        {
            doc.GetArray("a").Insert(0, i);
        }

        var gapped = YUpdate.Merge([updates[0], updates[2]], format: UpdateFormat.V2);

        var structs = YUpdate.Decode(gapped, format: UpdateFormat.V2).Structs;

        Assert.AreEqual((ItemKind.Skip, 1), structs.Where(s => s.Kind == ItemKind.Skip).Select(s => (s.Kind, s.Length)).Single());
    }

    private static IEnumerable<DecodedStruct> Decodable(byte[] update)
    {
        foreach (var decode in new Func<byte[], DecodedUpdate>[] { u => YUpdate.Decode(u, format: UpdateFormat.V1), u => YUpdate.Decode(u, format: UpdateFormat.V2) })
        {
            DecodedUpdate decoded = null;
            try
            {
                decoded = decode(update);
            }
            catch (YUpdateException)
            {
            }

            foreach (var s in decoded?.Structs ?? [])
            {
                yield return s;
            }
        }
    }

    // Each update in its own format and converted to the other: the two decoders must read the same records.
    private static int CompareFormats(List<byte[]> inputs, Func<int, UpdateFormat> formatOf)
    {
        var structs = 0;
        for (var n = 0; n < inputs.Count; n++)
        {
            var from = formatOf(n);
            var to = from == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;
            var original = YUpdate.Decode(inputs[n], format: from);
            var converted = YUpdate.Decode(YUpdate.Convert(inputs[n], from, to), format: to);
            Assert.AreEqual(original.Structs.Count, converted.Structs.Count);
            for (var i = 0; i < original.Structs.Count; i++)
            {
                StagingDifferential.AssertEqual(original.Structs[i], converted.Structs[i]);
                AssertWellFormed(original.Structs[i]);
            }

            structs += original.Structs.Count;
        }

        return structs;
    }

    // What DecodedStruct says each kind carries, and that the rest is empty.
    private static void AssertWellFormed(DecodedStruct d)
    {
        var holdsValues = d.Kind is ItemKind.Any or ItemKind.Json or ItemKind.Binary or ItemKind.Embed;
        var actual = (
            d.Length > 0,
            holdsValues ? d.Content.Count == d.Length : d.Content.Count == 0,
            d.Kind == ItemKind.String ? d.Text != null && d.Text.Length == d.Length : d.Text == null,
            d.Kind == ItemKind.Format ? d.FormatKey != null : d.FormatKey == null && d.FormatValue == null,
            d.Kind == ItemKind.Type ? d.TypeRef >= 0 : d.TypeRef == -1 && d.TypeName == null,
            d.Kind == ItemKind.Doc ? d.DocGuid != null : d.DocGuid == null,
            d.Kind is ItemKind.GC or ItemKind.Skip
                ? d.LeftOrigin == null && d.RightOrigin == null && d.ParentName == null && d.ParentId == null && d.ParentSub == null
                : d.LeftOrigin != null || d.RightOrigin != null
                    ? d.ParentName == null && d.ParentId == null
                    : (d.ParentName == null) != (d.ParentId == null));
        Assert.AreEqual((true, true, true, true, true, true, true), actual, $"the record of {d.Id} ({d.Kind})");
    }

    internal static List<byte[]> CorpusUpdates()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "interop", "corpus", "cases")))
        {
            dir = dir.Parent;
        }

        Assert.IsNotNull(dir, "interop/corpus/cases");
        var result = new List<byte[]>();
        // Sorted: Directory.GetFiles' order depends on the file system, and the damaged inputs are drawn by index.
        foreach (var file in Directory.GetFiles(Path.Combine(dir.FullName, "interop", "corpus", "cases"), "*.json").Order(StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var update in json.RootElement.GetProperty("updates").EnumerateArray())
            {
                foreach (var name in new[] { "v1", "v2" })
                {
                    result.Add(Convert.FromBase64String(update.GetProperty(name).GetString()!));
                }
            }
        }

        return result;
    }

    internal static List<byte[]> RichUpdates()
    {
        var result = new List<byte[]>();
        foreach (var gc in new[] { true, false })
        {
            var doc = new YDoc(new YDocOptions { ClientId = 7, GarbageCollection = gc });
            doc.GetText("t").Insert(0, "hello é😀 world");
            doc.GetText("t").Format(1, 3, new Dictionary<string, YValue> { ["bold"] = true });
            doc.GetText("t").InsertEmbed(2, YValue.FromObject(new Dictionary<string, object> { ["img"] = "x" }));
            doc.GetArray("a").Add(1, "two", 3.5, YValue.Null, true, new byte[] { 1, 2, 3 }, 1);
            doc.GetArray("a").Add(new YDoc(new YDocOptions { Guid = "sub-1" }));
            var map = doc.GetMap("m");
            map["k"] = "v";
            map["nested"] = new YMap();
            map["arr"] = new YArray();
            map["txt"] = new YText("abc");
            map["k"] = "w";
            ((YMap)map["nested"].ToObject())["inner"] = "gone";
            map.Remove("nested");
            var xml = doc.GetXmlFragment("x");
            var element = new YXmlElement("div");
            xml.Insert(0, element, new YXmlText("t"), new YXmlHook("hk"), new YXmlFragment());
            doc.GetText("t").Remove(4, 5);
            doc.GetArray("a").RemoveRange(0, 2);
            var peer = new YDoc(new YDocOptions { ClientId = 8, GarbageCollection = gc });
            peer.ApplyUpdate(doc.EncodeStateAsUpdate());
            peer.GetText("t").Insert(1, "peer");
            doc.ApplyUpdate(peer.EncodeStateAsUpdate());
            result.Add(doc.EncodeStateAsUpdate());
            result.Add(doc.EncodeStateAsUpdate(format: UpdateFormat.V2));
        }

        var gapped = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        gapped.UpdatedV2 += (_, e) => updates.Add(e.Update.ToArray());
        for (var i = 0; i < 4; i++)
        {
            gapped.GetArray("a").Insert(0, i);
        }

        result.Add(YUpdate.Merge([updates[0], updates[2]], format: UpdateFormat.V2));
        return result;
    }

    internal static byte[] Damage(byte[] update, Random random)
    {
        var bytes = update.ToList();
        for (var n = 1 + random.Next(3); n > 0 && bytes.Count > 0; n--)
        {
            var at = random.Next(bytes.Count);
            switch (random.Next(4))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 2:
                    bytes.Insert(at, (byte)random.Next(256));
                    break;
                default:
                    bytes[at] = (byte)random.Next(256);
                    break;
            }
        }

        return bytes.ToArray();
    }
}
