using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A small seeded fuzz loop over everything that decodes untrusted bytes besides applying (which
/// tests/Ycs.Fuzz covers in depth): merging, diffing, state vectors from updates and as input,
/// meta, decoding and format conversion, in v1 and v2. Only <see cref="YUpdateException"/> may come
/// out, no input may take long or allocate much, and a failed apply leaves the document unchanged.
/// </summary>
[TestClass]
public class UpdateOperationsFuzzTests
{
    // YCS_HARDENING_ITERATIONS / YCS_HARDENING_SEED for a soak.
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("YCS_HARDENING_ITERATIONS"), out var n) ? n : 3000;
    private static readonly int RandomSeed = int.TryParse(Environment.GetEnvironmentVariable("YCS_HARDENING_SEED"), out var s) ? s : 12345;
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);
    private const long AllocationBudget = 64L << 20;

    private static List<(byte[] V1, byte[] V2)> SeedUpdates()
    {
        var seeds = new List<(byte[], byte[])>();
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        a.Updated += (_, e) => seeds.Add((e.Update.ToArray(), YUpdate.Convert(e.Update.ToArray(), UpdateFormat.V1, UpdateFormat.V2)));

        a.GetText("t").Insert(0, "hello world");
        a.GetText("t").Format(0, 5, new Dictionary<string, YValue> { ["bold"] = true });
        a.GetText("t").Remove(2, 3);
        a.GetMap("m")["k"] = YValue.FromObject(new Dictionary<string, object> { ["n"] = 1.5, ["l"] = new List<object> { "x", null, true } });
        a.GetArray("a").Insert(0, 1, "two", new byte[] { 3 }, new YMap());
        a.GetArray("a").RemoveRange(1, 1);
        var xml = new YXmlElement("p");
        a.GetXmlFragment("x").Insert(0, new[] { xml });
        xml.SetAttribute("class", "c");

        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(3, "BBB");
        var full = b.EncodeStateAsUpdate();
        seeds.Add((full, b.EncodeStateAsUpdate(format: UpdateFormat.V2)));
        return seeds;
    }

    private static byte[] Mutate(byte[] input, Random random)
    {
        var bytes = input.ToList();
        switch (random.Next(5))
        {
            case 0: // bit flips
                for (var n = random.Next(1, 4); n > 0 && bytes.Count > 0; n--)
                {
                    var i = random.Next(bytes.Count);
                    bytes[i] ^= (byte)(1 << random.Next(8));
                }

                break;
            case 1: // truncation
                bytes = bytes.Take(random.Next(bytes.Count + 1)).ToList();
                break;
            case 2: // a huge var-uint
                {
                    var i = random.Next(bytes.Count + 1);
                    bytes.InsertRange(i, random.Next(2) == 0 ? new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F } : new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F });
                    break;
                }

            case 3: // random byte
                if (bytes.Count > 0)
                {
                    bytes[random.Next(bytes.Count)] = (byte)random.Next(256);
                }

                break;
            default: // duplicated chunk
                if (bytes.Count > 0)
                {
                    var start = random.Next(bytes.Count);
                    var chunk = bytes.Skip(start).Take(random.Next(1, 16)).ToList();
                    bytes.InsertRange(random.Next(bytes.Count + 1), chunk);
                }

                break;
        }

        return bytes.ToArray();
    }

    private static string StateOf(YDoc doc) =>
        doc.GetText("t") + "|" + Convert.ToHexString(doc.EncodeStateVector()) + "|" + Convert.ToHexString(doc.EncodeStateAsUpdate());

    private static IEnumerable<(string Name, Action Run)> Operations(byte[] mutated, (byte[] V1, byte[] V2) valid, bool v2, YDoc doc)
    {
        if (v2)
        {
            yield return ("MergeUpdatesV2", () => YUpdate.Merge([mutated, valid.V2], format: UpdateFormat.V2));
            yield return ("DiffUpdateV2", () => YUpdate.Diff(mutated, new byte[] { 0 }, format: UpdateFormat.V2));
            yield return ("DiffUpdateV2 sv", () => YUpdate.Diff(valid.V2, mutated, format: UpdateFormat.V2));
            yield return ("EncodeStateVectorFromUpdateV2", () => YUpdate.EncodeStateVector(mutated, format: UpdateFormat.V2));
            yield return ("ParseUpdateMetaV2", () => YUpdate.ParseMeta(mutated, format: UpdateFormat.V2));
            yield return ("ConvertUpdateFormatV2ToV1", () => YUpdate.Convert(mutated, UpdateFormat.V2, UpdateFormat.V1));
            yield return ("ApplyUpdateV2", () => doc.ApplyUpdate(mutated, format: UpdateFormat.V2));
        }
        else
        {
            yield return ("MergeUpdates", () => YUpdate.Merge([mutated, valid.V1], format: UpdateFormat.V1));
            yield return ("DiffUpdate", () => YUpdate.Diff(mutated, new byte[] { 0 }, format: UpdateFormat.V1));
            yield return ("DiffUpdate sv", () => YUpdate.Diff(valid.V1, mutated, format: UpdateFormat.V1));
            yield return ("EncodeStateVectorFromUpdate", () => YUpdate.EncodeStateVector(mutated, format: UpdateFormat.V1));
            yield return ("ParseUpdateMeta", () => YUpdate.ParseMeta(mutated, format: UpdateFormat.V1));
            yield return ("DecodeUpdate", () => YUpdate.Decode(mutated, format: UpdateFormat.V1));
            yield return ("ConvertUpdateFormatV1ToV2", () => YUpdate.Convert(mutated, UpdateFormat.V1, UpdateFormat.V2));
            yield return ("EncodeStateAsUpdate sv", () => doc.EncodeStateAsUpdate(mutated));
            yield return ("ApplyUpdate", () => doc.ApplyUpdate(mutated));
        }
    }

    [TestMethod]
    public void MutatedUpdates_UpdateOperations_FailCleanly()
    {
        var seeds = SeedUpdates();
        var random = new Random(RandomSeed);
        var failures = new List<string>();

        var run = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < Iterations && failures.Count < 10; i++)
            {
                var seed = seeds[random.Next(seeds.Count)];
                var v2 = random.Next(2) == 0;
                var mutated = Mutate(v2 ? seed.V2 : seed.V1, random);
                var doc = new YDoc(new YDocOptions { ClientId = 99 });
                doc.ApplyUpdate(seeds[0].V1);

                foreach (var (name, op) in Operations(mutated, seed, v2, doc))
                {
                    var before = StateOf(doc);
                    var stopwatch = Stopwatch.StartNew();
                    var allocated = System.GC.GetAllocatedBytesForCurrentThread();
                    var failed = false;
                    try
                    {
                        op();
                    }
                    catch (YUpdateException)
                    {
                        failed = true;
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{name}: {e.GetType().Name} for {Convert.ToHexString(mutated)}: {e.Message}");
                        continue;
                    }

                    allocated = System.GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (stopwatch.Elapsed > TimeBudget || allocated > AllocationBudget)
                    {
                        failures.Add($"{name}: {stopwatch.ElapsedMilliseconds} ms, {allocated} bytes for {Convert.ToHexString(mutated)}");
                    }

                    if (failed && StateOf(doc) != before)
                    {
                        failures.Add($"{name}: the document changed for {Convert.ToHexString(mutated)}");
                    }
                }
            }
        }, TaskCreationOptions.LongRunning);

        Assert.IsTrue(run.Wait(TimeSpan.FromMinutes(2) + Iterations * TimeSpan.FromMilliseconds(1)), "the loop did not finish (a hang)");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
