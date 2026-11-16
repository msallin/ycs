using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Values and shared types nested as deeply as Yjs handles (<see cref="UpdateLimits.MaxDepth"/>): documents Yjs wrote
/// (Fixtures/deep-nesting.json, interop/js/deep-gen.mjs) read and write byte for byte, one level more is rejected, and
/// hostile updates of 100,000 levels are rejected, or with <see cref="UpdateLimits.Unlimited"/> applied, on a thread
/// with a 256 KiB stack.
/// </summary>
[TestClass]
public class DeepNestingTests
{
    private const int HostileDepth = 100_000;

    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "deep-nesting.json"))).RootElement);

    public static IEnumerable<object[]> Cases => [["values"], ["format"], ["embed"], ["maps"], ["arrays"], ["xml"]];

    [TestMethod]
    public void Fixture_IsAtTheDefaultLimits()
    {
        Assert.AreEqual(UpdateLimits.Default.MaxDepth, Fixture.Value.GetProperty("values").GetInt32());
        Assert.IsGreaterThanOrEqualTo(2000, Fixture.Value.GetProperty("types").GetInt32());
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void YjsDocument_AtTheLimit_RoundTripsByteForByte(string name)
    {
        var (v1, v2) = Bytes(name);
        var fromV1 = new YDoc(new YDocOptions { ClientId = 9 });
        var fromV2 = new YDoc(new YDocOptions { ClientId = 9 });

        fromV1.ApplyUpdate(v1);
        fromV2.ApplyUpdate(v2, format: UpdateFormat.V2);

        Assert.AreEqual(Convert.ToBase64String(v1), Convert.ToBase64String(fromV1.EncodeStateAsUpdate()));
        Assert.AreEqual(Convert.ToBase64String(v2), Convert.ToBase64String(fromV2.EncodeStateAsUpdate(format: UpdateFormat.V2)));
        Assert.AreEqual(Convert.ToBase64String(v1), Convert.ToBase64String(fromV2.EncodeStateAsUpdate()));
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void YjsDocument_AtTheLimit_ReadsDeletesAndCollectsOnASmallStack(string name)
    {
        var (v1, _) = Bytes(name);

        var error = OnSmallStack(() =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 9 });
            doc.ApplyUpdate(v1);
            _ = doc.GetMap("m").ToJson();
            _ = doc.GetArray("a").ToJson();
            _ = doc.GetText("t").ToDelta();
            doc.GetMap("m").Clear();
            doc.GetArray("a").Delete(0, doc.GetArray("a").Length);
            doc.GetText("t").Remove(0, doc.GetText("t").Length);
            doc.GetXmlFragment("x").Delete(0, doc.GetXmlFragment("x").Length);
            _ = doc.EncodeStateAsUpdate(format: UpdateFormat.V2);
        });

        Assert.IsNull(error, error?.ToString());
    }

    [TestMethod]
    public void Values_OneLevelBeyondTheLimit_AreRejected()
    {
        var update = Encoded(doc => doc.GetMap("m").Set("v", NestedValue(UpdateLimits.Default.MaxDepth + 1)));
        var doc = new YDoc();

        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(update));

        StringAssert.Contains(e.Message, "UpdateLimits.MaxDepth");
        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void Types_OneLevelBeyondTheLimit_AreRejected()
    {
        var update = Encoded(doc => NestMaps(doc.GetMap("m"), UpdateLimits.Default.MaxDepth + 1));
        var doc = new YDoc();

        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(YUpdate.Convert(update, UpdateFormat.V1, UpdateFormat.V2, limits: UpdateLimits.Unlimited), format: UpdateFormat.V2));

        StringAssert.Contains(e.Message, "UpdateLimits.MaxDepth");
        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void LocalInsert_BeyondTheDocumentsLimit_Throws()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 3 } });

        Assert.ThrowsExactly<ArgumentException>(() => doc.GetMap("m").Set("v", NestedValue(4)));
        Assert.ThrowsExactly<InvalidOperationException>(() => NestMaps(doc.GetMap("n"), 4));
    }

    public static IEnumerable<object[]> HostileValues =>
    [
        ["map value v1", false],
        ["map value v2", true],
        ["format v1", false],
        ["format v2", true],
        ["embed v1", false],
        ["types v1", false],
        ["types v2", true],
    ];

    [TestMethod]
    [DynamicData(nameof(HostileValues))]
    public void Hostile_100000Levels_AreRejectedOnASmallStack(string what, bool v2)
    {
        var update = Hostile(what, v2);
        Exception thrown = null;

        var error = OnSmallStack(() =>
        {
            var doc = new YDoc();
            try
            {
                Apply(doc, update, v2, limits: null);
            }
            catch (YUpdateException e)
            {
                thrown = e;
            }

            Assert.AreEqual(Convert.ToBase64String(new YDoc().EncodeStateAsUpdate()), Convert.ToBase64String(doc.EncodeStateAsUpdate()));
        });

        Assert.IsNull(error, error?.ToString());
        Assert.IsInstanceOfType<YUpdateException>(thrown);
    }

    [TestMethod]
    [DynamicData(nameof(HostileValues))]
    public void Unlimited_100000Levels_ApplyReadAndEncodeOnASmallStack(string what, bool v2)
    {
        var update = Hostile(what, v2);
        byte[] encoded = null;

        var error = OnSmallStack(() =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 9, Limits = UpdateLimits.Unlimited });
            Apply(doc, update, v2, UpdateLimits.Unlimited);
            _ = doc.GetMap("m").ToJson();
            _ = doc.GetText("t").ToDelta();
            encoded = v2 ? doc.EncodeStateAsUpdate(format: UpdateFormat.V2) : doc.EncodeStateAsUpdate();
            doc.GetMap("m").Clear();
            doc.GetText("t").Remove(0, doc.GetText("t").Length);
        });

        Assert.IsNull(error, error?.ToString());
        Assert.AreEqual(Convert.ToBase64String(update), Convert.ToBase64String(encoded));
    }

    private static readonly Dictionary<(string, bool), byte[]> HostileCache = new();

    private static byte[] Hostile(string what, bool v2)
    {
        lock (HostileCache)
        {
            if (!HostileCache.TryGetValue((what, v2), out var update))
            {
                HostileCache[(what, v2)] = update = Encoded(doc =>
                {
                    switch (what.Split(' ')[0])
                    {
                        case "map":
                            doc.GetMap("m").Set("v", NestedValue(HostileDepth));
                            break;
                        case "format":
                            doc.GetText("t").Insert(0, "abc", new Dictionary<string, YValue> { ["deep"] = NestedYValue(HostileDepth) });
                            break;
                        case "embed":
                            doc.GetText("t").InsertEmbed(0, YValue.Object(new KeyValuePair<string, YValue>("deep", NestedYValue(HostileDepth))));
                            break;
                        default:
                            NestMaps(doc.GetMap("m"), HostileDepth);
                            break;
                    }
                }, v2);
            }

            return update;
        }
    }

    private static void Apply(YDoc doc, byte[] update, bool v2, UpdateLimits limits)
    {
        if (v2)
        {
            doc.ApplyUpdate(update, format: UpdateFormat.V2, limits: limits);
        }
        else
        {
            doc.ApplyUpdate(update, limits: limits);
        }
    }

    private static (byte[] V1, byte[] V2) Bytes(string name)
    {
        var c = Fixture.Value.GetProperty("cases").GetProperty(name);
        return (Convert.FromBase64String(c.GetProperty("v1").GetString()), Convert.FromBase64String(c.GetProperty("v2").GetString()));
    }

    private static byte[] Encoded(Action<YDoc> build, bool v2 = false)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, Limits = UpdateLimits.Unlimited });
        // One transaction: a transaction per level would walk the parents of the changed type each time (as Yjs'
        // callTypeObservers does), quadratic in the depth.
        doc.Transact(_ => build(doc));
        return v2 ? doc.EncodeStateAsUpdate(format: UpdateFormat.V2) : doc.EncodeStateAsUpdate();
    }

    // Lists and dictionaries in turn, `depth` levels below the top.
    // As NestedValue, built from the inside out: YValue.FromObject refuses a depth beyond the limits' default.
    private static YValue NestedYValue(int depth)
    {
        if (depth == 0)
        {
            return YValue.Array();
        }

        var current = (depth - 1) % 2 == 0 ? YValue.Object() : YValue.Array();
        for (var i = depth - 2; i >= 0; i--)
        {
            current = i % 2 == 0 ? YValue.Object(new KeyValuePair<string, YValue>("k", current)) : YValue.Array(current);
        }

        return YValue.Array(current);
    }

    private static List<object> NestedValue(int depth)
    {
        var root = new List<object>();
        object current = root;
        for (var i = 0; i < depth; i++)
        {
            object next = i % 2 == 0 ? new Dictionary<string, object>() : new List<object>();
            if (current is List<object> list)
            {
                list.Add(next);
            }
            else
            {
                ((Dictionary<string, object>)current)["k"] = next;
            }

            current = next;
        }

        return root;
    }

    private static void NestMaps(YMap map, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            var child = new YMap();
            map.Set("c", child);
            map = child;
        }
    }

    // Runs `action` on a thread with a 256 KiB stack; returns what it threw. A stack overflow ends the process.
    private static Exception OnSmallStack(Action action)
    {
        Exception error = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
            },
            256 * 1024);
        thread.Start();
        thread.Join();
        return error;
    }
}
