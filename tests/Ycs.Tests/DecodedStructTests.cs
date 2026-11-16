using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="DecodedStruct"/> against the struct objects it detaches from: every field of every record, over the
/// corpus updates, documents that hold every content kind, gapped merges (Skip) and damaged updates.
/// </summary>
[TestClass]
public class DecodedStructTests
{
    private const int Damaged = 20000;

    [TestMethod]
    public void Detach_CorpusUpdates_EveryFieldEqualsTheObject()
    {
        var inputs = CorpusUpdates();

        var (updates, structs) = Compare(inputs);

        Assert.IsGreaterThan(100, updates);
        Assert.IsGreaterThan(500, structs);
    }

    [TestMethod]
    public void Detach_EveryContentKind_EveryFieldEqualsTheObjectAndTheKindsAreAllSeen()
    {
        var inputs = RichUpdates();

        var (_, _) = Compare(inputs);

        var kinds = inputs
            .SelectMany(Decodable)
            .Select(s => s.Kind)
            .Distinct()
            .Order()
            .ToList();
        CollectionAssert.AreEqual(Enum.GetValues<ItemKind>().Where(k => k != ItemKind.Json).Order().ToList(), kinds);
    }

    [TestMethod]
    public void Detach_DamagedUpdates_EveryDecodedFieldEqualsTheObject()
    {
        var seeds = CorpusUpdates().Concat(RichUpdates()).ToList();
        var random = new Random(20261116);
        var inputs = new List<byte[]>();
        while (inputs.Count < Damaged)
        {
            inputs.Add(Damage(seeds[random.Next(seeds.Count)], random));
        }

        var (updates, structs) = Compare(inputs);

        Assert.IsGreaterThan(1000, updates, "enough damaged updates still decode");
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
            doc.GetArray("a").Insert(0, new List<object> { i });
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

    private static (int Updates, int Structs) Compare(IEnumerable<byte[]> inputs)
    {
        var updates = 0;
        var structs = 0;
        foreach (var input in inputs)
        {
            foreach (var v2 in new[] { false, true })
            {
                var raw = ReadObjects(input, v2, out var rawError);
                DecodedUpdate decoded;
                try
                {
                    decoded = v2 ? YUpdate.Decode(input, format: UpdateFormat.V2) : YUpdate.Decode(input, format: UpdateFormat.V1);
                }
                catch (YUpdateException e)
                {
                    if (rawError != null)
                    {
                        Assert.AreEqual(rawError, e.Message, "the same exception as the struct objects' read");
                    }

                    continue;
                }

                Assert.IsNull(rawError, "decoding succeeds where the struct objects' read fails");
                var detached = decoded.Structs;
                Assert.HasCount(raw.Count, detached);
                for (var i = 0; i < detached.Count; i++)
                {
                    AssertEqual(raw[i], detached[i]);
                }

                updates++;
                structs += detached.Count;
            }
        }

        return (updates, structs);
    }

    // The structs as objects, in the order of the update, up to the one that fails.
    private static List<AbstractStruct> ReadObjects(byte[] input, bool v2, out string rawError)
    {
        var limits = UpdateLimits.Default;
        var reader = new Lib0Reader(input) { MaxDepth = limits.MaxDepth };
        var result = new List<AbstractStruct>();
        rawError = null;
        try
        {
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader, limits.MaxStructsPerUpdate) : new UpdateDecoderV1(reader, limits.MaxStructsPerUpdate);
            var cursor = new EncodingUtils.StructCursor(decoder);
            while (cursor.Next() is { } str)
            {
                result.Add(str);
            }
        }
        catch (Exception e) when (e is YUpdateException || YUpdateException.IsDecodingFailure(e))
        {
            rawError = YUpdateException.Wrap(e).Message;
        }

        return result;
    }

    // The oracle reads the objects with the wire's own numbers (IContentEx.Ref) and their public members.
    private static void AssertEqual(AbstractStruct s, DecodedStruct d)
    {
        Assert.AreEqual(s.Id, d.Id);
        Assert.AreEqual(s.Length, d.Length);
        if (s is not Item item)
        {
            Assert.AreEqual(s is Skip ? ItemKind.Skip : ItemKind.GC, d.Kind);
            Assert.IsTrue(d.LeftOrigin == null && d.RightOrigin == null && d.ParentName == null && d.ParentId == null && d.ParentSub == null);
            Assert.IsEmpty(d.Content);
            Assert.IsTrue(d.Text == null && d.FormatKey == null && d.FormatValue == null && d.TypeRef == -1 && d.TypeName == null && d.DocGuid == null);
            return;
        }

        var content = item.Content;
        Assert.AreEqual((ItemKind)content.Ref, d.Kind);
        Assert.AreEqual(item.LeftOrigin, d.LeftOrigin);
        Assert.AreEqual(item.RightOrigin, d.RightOrigin);
        Assert.AreEqual(item.Parent as string, d.ParentName);
        Assert.AreEqual(item.Parent is ItemId id ? id : (ItemId?)null, d.ParentId);
        Assert.AreEqual(item.ParentSub, d.ParentSub);

        var holdsValues = d.Kind is ItemKind.Any or ItemKind.Json or ItemKind.Binary or ItemKind.Embed;
        if (holdsValues)
        {
            Assert.AreEqual(item.Length, d.Content.Count);
        }

        var expectedValues = holdsValues ? content.GetContent().ToList() : new List<object>();
        Assert.HasCount(expectedValues.Count, d.Content);
        for (var i = 0; i < expectedValues.Count; i++)
        {
            Assert.IsTrue(StagingDifferential.DeepEqual(expectedValues[i], d.Content[i]), $"value {i} of {s.Id}");
        }

        Assert.AreEqual(d.Kind == ItemKind.String ? ((ContentString)content).GetString() : null, d.Text);
        Assert.AreEqual(d.Kind == ItemKind.Format ? ((ContentFormat)content).Key : null, d.FormatKey);
        Assert.AreEqual(d.Kind == ItemKind.Format ? ((ContentFormat)content).Value : null, d.FormatValue);
        var type = d.Kind == ItemKind.Type ? ((ContentType)content).Type : null;
        Assert.AreEqual(type == null ? -1 : type.GetType().Name switch
        {
            "YArray" => 0,
            "YMap" => 1,
            "YText" => 2,
            "YXmlElement" => 3,
            "YXmlFragment" => 4,
            "YXmlHook" => 5,
            "YXmlText" => 6,
            var other => throw new AssertFailedException(other),
        }, d.TypeRef);
        Assert.AreEqual((type as YXmlElement)?.NodeName ?? (type as YXmlHook)?.HookName, d.TypeName);
        Assert.AreEqual(d.Kind == ItemKind.Doc ? ((ContentDoc)content).Doc.Guid : null, d.DocGuid);
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
        foreach (var file in Directory.GetFiles(Path.Combine(dir.FullName, "interop", "corpus", "cases"), "*.json"))
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
            var doc = new YDoc(new YDocOptions { ClientId = 7, Gc = gc });
            doc.GetText("t").Insert(0, "hello é😀 world");
            doc.GetText("t").Format(1, 3, new Dictionary<string, object> { ["bold"] = true });
            doc.GetText("t").InsertEmbed(2, new Dictionary<string, object> { ["img"] = "x" });
            doc.GetArray("a").Push(new List<object> { 1, "two", 3.5, null, true, new byte[] { 1, 2, 3 }, new List<object> { 1 } });
            doc.GetArray("a").Push(new List<object> { new YDoc(new YDocOptions { Guid = "sub-1" }) });
            var map = doc.GetMap("m");
            map.Set("k", "v");
            map.Set("nested", new YMap());
            map.Set("arr", new YArray());
            map.Set("txt", new YText("abc"));
            map.Set("k", "w");
            ((YMap)map.Get("nested")).Set("inner", "gone");
            map.Delete("nested");
            var xml = doc.GetXmlFragment("x");
            var element = new YXmlElement("div");
            xml.Insert(0, new List<object> { element, new YXmlText("t"), new YXmlHook("hk"), new YXmlFragment() });
            doc.GetText("t").Delete(4, 5);
            doc.GetArray("a").Delete(0, 2);
            var peer = new YDoc(new YDocOptions { ClientId = 8, Gc = gc });
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
            gapped.GetArray("a").Insert(0, new List<object> { i });
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
