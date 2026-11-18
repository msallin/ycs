using System.Diagnostics.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// <see cref="IUpdateDecoder.ReadStringChars"/> against <see cref="IUpdateDecoder.ReadString"/>: two decoders over
/// the same bytes read in lock step, and every string and every failure must agree.
/// </summary>
[TestClass]
public class StringSpanDifferentialTests
{
    private const int MutationsPerUpdate = 200;

    public static IEnumerable<object[]> Cases => CorpusCase.Names;

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ReadStringSpan_CorpusUpdates_EqualsReadString(string name)
    {
        var c = CorpusCase.Named(name);
        foreach (var update in c.Updates.Concat([c.ExpectedState, c.Diff, c.Merged]))
        {
            foreach (var v2 in new[] { false, true })
            {
                var result = Compare(update.Get(v2), v2);
                Assert.IsFalse(result.Failed, "a recorded update decodes");
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ReadStringSpan_FuzzedCorpusUpdates_EqualsReadStringAndFailsAlike(string name)
    {
        var c = CorpusCase.Named(name);
        var random = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x5eed);
        var strings = 0;
        var inputs = 0;
        foreach (var update in c.Updates)
        {
            foreach (var v2 in new[] { false, true })
            {
                var bytes = update.Get(v2);
                for (var i = 0; i < MutationsPerUpdate; i++)
                {
                    strings += Compare(Mutate(bytes, random), v2).Strings;
                    inputs++;
                }
            }
        }

        Console.WriteLine($"ReadStringSpan fuzz {name}: {inputs} inputs, {strings} strings.");
    }

    [TestMethod]
    public void ReadStringSpan_CorpusAndFuzz_CountsAreReported()
    {
        var strings = 0;
        var failures = 0;
        var random = new Random(1);
        foreach (var c in CorpusCase.All)
        {
            foreach (var update in c.Updates)
            {
                foreach (var v2 in new[] { false, true })
                {
                    var bytes = update.Get(v2);
                    var plain = Compare(bytes, v2);
                    strings += plain.Strings;
                    for (var i = 0; i < 20; i++)
                    {
                        var r = Compare(Mutate(bytes, random), v2);
                        strings += r.Strings;
                        failures += r.Failed ? 1 : 0;
                    }
                }
            }
        }

        Console.WriteLine($"ReadStringSpan differential: {strings} strings compared, {failures} failing inputs agreed.");
        Assert.IsGreaterThan(0, strings);
    }

    private static byte[] Mutate(byte[] input, Random random)
    {
        var data = (byte[])input.Clone();
        switch (random.Next(4))
        {
            case 0:
                return data.Length == 0 ? data : data[..random.Next(data.Length)];
            case 1:
                for (var i = 0; i < 1 + random.Next(4) && data.Length > 0; i++)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                return data;
            case 2:
                for (var i = 0; i < 1 + random.Next(4) && data.Length > 0; i++)
                {
                    // Bytes that make UTF-8 invalid or lengths huge.
                    data[random.Next(data.Length)] = new byte[] { 0xC0, 0xC1, 0xED, 0xF5, 0xFF, 0x80, 0xBF, 0x7F }[random.Next(8)];
                }

                return data;
            default:
                for (var i = 0; i < 1 + random.Next(3) && data.Length > 0; i++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                return data;
        }
    }

    private static (int Strings, bool Failed) Compare(byte[] bytes, bool v2)
    {
        var expected = Run(bytes, v2, span: false);
        var actual = Run(bytes, v2, span: true);

        CollectionAssert.AreEqual(expected.Log, actual.Log, "strings and failures per read");
        Assert.AreEqual(expected.Failure?.GetType(), actual.Failure?.GetType(), "exception type");
        Assert.AreEqual(expected.Failure?.Message, actual.Failure?.Message, "exception message");
        Assert.AreEqual(expected.Remaining, actual.Remaining, "position");
        return (expected.Log.Count(l => l.StartsWith("s:", StringComparison.Ordinal)), expected.Failure != null);
    }

    private static (List<string> Log, Exception Failure, int Remaining) Run(byte[] bytes, bool v2, bool span)
    {
        var log = new List<string>();
        Exception failure = null;
        var remaining = 0;
        try
        {
            remaining = v2
                ? Drive(new UpdateDecoderV2(bytes, 1 << 20, UpdateLimits.DefaultMaxDepth, []), bytes, span, log, out failure)
                : Drive(new UpdateDecoderV1(bytes, 1 << 20, UpdateLimits.DefaultMaxDepth), bytes, span, log, out failure);
        }
        catch (YUpdateException e)
        {
            failure = e;
        }
        catch (OverflowException e)
        {
            failure = e;
        }

        return (log, failure, remaining);
    }

    private static int Drive<TInner>(TInner inner, byte[] bytes, bool span, List<string> log, out Exception failure)
        where TInner : struct, IUpdateDecoder
    {
        var logging = new Logging<TInner>(inner, span, log);
        failure = null;
        try
        {
            var cursor = EncodingUtils.StructCursor.Begin(ref logging, bytes, new StagingTable());
            while (cursor.NextStaged(ref logging, bytes) >= 0)
            {
            }
        }
        catch (YUpdateException e)
        {
            failure = e;
        }
        catch (OverflowException e)
        {
            failure = e;
        }

        return logging.Reader.Remaining;
    }

    /// <summary>Delegates to the decoder; a string is read with <see cref="IUpdateDecoder.ReadStringChars"/> or <see cref="IUpdateDecoder.ReadString"/> and logged, a failure with its position.</summary>
    private struct Logging<TInner> : IUpdateDecoder
        where TInner : struct, IUpdateDecoder
    {
        private TInner _inner;
        private readonly bool _span;
        private readonly List<string> _log;
        private string _pending = "";

        public Logging(TInner inner, bool span, List<string> log)
        {
            _inner = inner;
            _span = span;
            _log = log;
        }

        [UnscopedRef]
        public ref Lib0Reader Reader => ref _inner.Reader;

        public readonly long MaxStructs => _inner.MaxStructs;

        public string ReadString(ReadOnlySpan<byte> input)
        {
            try
            {
                var value = _inner.ReadString(input);
                _log.Add("s:" + value);
                return value;
            }
            catch (Exception e) when (e is YUpdateException or OverflowException)
            {
                throw Failed(e);
            }
        }

        // The reference reads the whole string here; the split path only the bound, and logs the characters in ReadStringChars.
        public int ReadStringBound(ReadOnlySpan<byte> input)
        {
            if (!_span)
            {
                return (_pending = ReadString(input)).Length;
            }

            try
            {
                return _inner.ReadStringBound(input);
            }
            catch (Exception e) when (e is YUpdateException or OverflowException)
            {
                throw Failed(e);
            }
        }

        public int ReadStringExact(ReadOnlySpan<byte> input)
        {
            if (!_span)
            {
                return _pending.Length;
            }

            try
            {
                return _inner.ReadStringExact(input);
            }
            catch (Exception e) when (e is YUpdateException or OverflowException)
            {
                throw Failed(e);
            }
        }

        public int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination)
        {
            if (!_span)
            {
                _pending.CopyTo(destination);
                return _pending.Length;
            }

            var room = new char[destination.Length];
            try
            {
                var written = _inner.ReadStringChars(input, room);
                _log.Add("s:" + new string(room, 0, written));
                room.AsSpan(0, written).CopyTo(destination);
                return written;
            }
            catch (Exception e) when (e is YUpdateException or OverflowException)
            {
                throw Failed(e);
            }
        }

        // A failure is logged with its type, message and position, and thrown again.
        private Exception Failed(Exception e)
        {
            _log.Add("e:" + e.GetType().Name + ":" + e.Message + ":" + _inner.Reader.Remaining);
            return e;
        }

        public void ResetDsCurVal() => _inner.ResetDsCurVal();

        public long ReadDsClock(ReadOnlySpan<byte> input) => _inner.ReadDsClock(input);

        public long ReadDsLength(ReadOnlySpan<byte> input) => _inner.ReadDsLength(input);

        public ItemId ReadLeftId(ReadOnlySpan<byte> input) => _inner.ReadLeftId(input);

        public ItemId ReadRightId(ReadOnlySpan<byte> input) => _inner.ReadRightId(input);

        public long ReadClient(ReadOnlySpan<byte> input) => _inner.ReadClient(input);

        public byte ReadInfo(ReadOnlySpan<byte> input) => _inner.ReadInfo(input);

        public bool ReadParentInfo(ReadOnlySpan<byte> input) => _inner.ReadParentInfo(input);

        public uint ReadTypeRef(ReadOnlySpan<byte> input) => _inner.ReadTypeRef(input);

        public int ReadLength(ReadOnlySpan<byte> input) => _inner.ReadLength(input);

        public object ReadAny(ReadOnlySpan<byte> input) => _inner.ReadAny(input);

        public byte[] ReadBuffer(ReadOnlySpan<byte> input) => _inner.ReadBuffer(input);

        public string ReadKey(ReadOnlySpan<byte> input) => _inner.ReadKey(input);

        public object ReadJson(ReadOnlySpan<byte> input) => _inner.ReadJson(input);
    }
}
