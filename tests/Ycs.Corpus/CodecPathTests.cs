using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// The allocation-free encode and decode paths (<see cref="UpdateReader"/>, <see cref="EncodingUtils.EncodeStateAsUpdate(StructStore, ReadOnlySpan{byte}, bool)"/>,
/// the <see cref="IBufferWriter{T}"/> variants, <see cref="DeleteSet.WriteFromStore"/>) against Yjs'
/// bytes on every recorded case, ahead of <see cref="YDoc"/> calling them.
/// </summary>
[TestClass]
public class CodecPathTests
{
    public static IEnumerable<object[]> Cases => CorpusCase.Names;

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateAsUpdate_ReusedReaderAndEncoders_BytesEqualYjs(string name) => Run(name, c =>
    {
        foreach (var v2 in new[] { false, true })
        {
            var store = BuildWithReader(c, v2).Store;
            var expected = c.ExpectedState.Get(v2);
            AssertBytes(expected, EncodingUtils.EncodeStateAsUpdate(store, default, v2), $"array, v2: {v2}");
            AssertBytes(expected, IntoBufferWriter(w => EncodingUtils.EncodeStateAsUpdate(store, w, default, v2), chunk: 7), $"buffer writer, v2: {v2}");
            AssertBytes(c.ExpectedStateVectorBytes, EncodingUtils.EncodeStateVector(store), "state vector");
            AssertBytes(c.ExpectedStateVectorBytes, IntoBufferWriter(w => EncodingUtils.EncodeStateVector(store, w), chunk: 3), "state vector, buffer writer");
        }
    });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateAsUpdate_ForStateVector_BytesEqualTheDocumentsEncoding(string name) => Run(name, c =>
    {
        foreach (var v2 in new[] { false, true })
        {
            var doc = BuildWithReader(c, v2);
            var expected = v2 ? doc.EncodeStateAsUpdate(c.DiffStateVector, format: UpdateFormat.V2) : doc.EncodeStateAsUpdate(c.DiffStateVector);
            AssertBytes(expected, EncodingUtils.EncodeStateAsUpdate(doc.Store, c.DiffStateVector, v2), $"array, v2: {v2}");
            AssertBytes(expected, IntoBufferWriter(w => EncodingUtils.EncodeStateAsUpdate(doc.Store, w, c.DiffStateVector, v2), chunk: 1), $"buffer writer, v2: {v2}");
        }
    });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void WriteFromStore_BytesEqualTheMaterializedDeleteSet(string name) => Run(name, c =>
    {
        var store = BuildWithReader(c, v2: false).Store;
        foreach (var v2 in new[] { false, true })
        {
            IDSEncoder materialized = v2 ? new DSEncoderV2() : new DSEncoderV1();
            new DeleteSet(store).Write(materialized);
            IDSEncoder streamed = v2 ? new DSEncoderV2() : new DSEncoderV1();
            DeleteSet.WriteFromStore(streamed, store);
            AssertBytes(materialized.ToArray(), streamed.ToArray(), $"v2: {v2}");
        }
    });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void UpdateReader_RecordedUpdates_ReadsWhatFreshDecodersRead(string name) => Run(name, c =>
    {
        foreach (var v2 in new[] { false, true })
        {
            foreach (var update in c.Updates.Append(c.ExpectedState).Append(c.Merged))
            {
                var bytes = update.Get(v2);
                var (expectedStructs, expectedDs) = v2
                    ? ReadFresh(new UpdateDecoderV2(bytes, long.MaxValue, UpdateLimits.DefaultMaxDepth, []), bytes)
                    : ReadFresh(new UpdateDecoderV1(bytes, long.MaxValue, UpdateLimits.DefaultMaxDepth), bytes);

                using var lease = UpdateReader.Read(bytes, v2, long.MaxValue);
                Assert.AreEqual(expectedStructs, Describe(lease.Staging));
                Assert.AreEqual(expectedDs, Describe(lease.DeleteSet));
            }
        }
    });

    private static (string Structs, string DeleteSet) ReadFresh<TDecoder>(TDecoder decoder, byte[] bytes)
        where TDecoder : struct, IUpdateDecoder
    {
        var fresh = new StagingTable();
        var cursor = EncodingUtils.StructCursor.Begin(ref decoder, bytes, fresh);
        while (cursor.NextStaged(ref decoder, bytes) >= 0)
        {
        }

        return (Describe(fresh), Describe(DeleteSet.Read(ref decoder, bytes)));
    }

    /// <summary>Like <see cref="CorpusTests.Build"/>, through the reused reader and the store, as <see cref="YDoc"/> will.</summary>
    private static YDoc BuildWithReader(CorpusCase c, bool v2)
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = c.Gc });
        foreach (var update in c.Updates)
        {
            var bytes = update.Get(v2);
            using var lease = UpdateReader.Read(bytes, v2, long.MaxValue);
            doc.Transact(tr =>
            {
                tr.Local = false;
                doc.Store.ApplyUpdate(tr, lease.Staging, lease.DeleteSet, UpdateLimits.Unlimited);
            }, origin: null, local: false);
        }

        return doc;
    }

    private static string Describe(StagingTable table) =>
        string.Join(";", Enumerable.Range(0, table.ClientCount).Select(client => table.ClientAt(client)).Order().Select(client =>
        {
            var (start, count) = table.RangeOf(table.FindClient(client));
            return $"{client}:" + string.Join(",", Enumerable.Range(start, count).Select(i => $"{table.At(i).Kind}@{table.At(i).Clock}+{table.At(i).Length}"));
        }));

    private static string Describe(DeleteSet ds) =>
        string.Join(";", ds.Clients.OrderBy(kvp => kvp.Key).Select(kvp =>
            $"{kvp.Key}:" + string.Join(",", kvp.Value.Select(d => $"{d.Clock}+{d.Length}"))));

    private static byte[] IntoBufferWriter(Action<IBufferWriter<byte>> write, int chunk)
    {
        var writer = new ChunkedBufferWriter(chunk);
        write(writer);
        return writer.ToArray();
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string what) =>
        Assert.AreEqual(Convert.ToHexString(expected), Convert.ToHexString(actual), what);

    private static void Run(string name, Action<CorpusCase> test, [CallerMemberName] string testName = "") =>
        KnownFailures.Check(name, testName, () => test(CorpusCase.Named(name)));

    /// <summary>Hands out the smallest memory the contract allows (at least the hint, else <c>chunk</c> bytes), so every chunk boundary is crossed.</summary>
    private sealed class ChunkedBufferWriter(int chunk) : IBufferWriter<byte>
    {
        private readonly List<byte> _written = new();
        private byte[] _current = [];

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _current.Length);
            _written.AddRange(_current.AsSpan(0, count));
            _current = [];
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _current = new byte[Math.Max(sizeHint, chunk)];

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public byte[] ToArray() => _written.ToArray();
    }
}
