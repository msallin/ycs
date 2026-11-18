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
        var reader = new Lib0Reader(bytes);
        var log = new List<string>();
        Exception failure = null;
        try
        {
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader, 1 << 20) : new UpdateDecoderV1(reader, 1 << 20);
            var logging = new Logging(decoder, span, log);
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

        return (log, failure, reader.Remaining);
    }

    private delegate string ReadFunc(ReadOnlySpan<byte> input);

    /// <summary>Delegates to the decoder; a string is read with <see cref="IUpdateDecoder.ReadStringChars"/> or <see cref="IUpdateDecoder.ReadString"/> and logged, a failure with its position.</summary>
    private sealed class Logging(IUpdateDecoder inner, bool span, List<string> log) : IUpdateDecoder
    {
        public Lib0Reader Reader => inner.Reader;

        public long MaxStructs => inner.MaxStructs;

        public string ReadString(ReadOnlySpan<byte> input) => Logged(inner.ReadString, input, true);

        private string pending = "";

        // The reference reads the whole string here; the split path only the bound, and logs the characters in ReadStringChars.
        public int ReadStringBound(ReadOnlySpan<byte> input) => span ? Logged(i => new string(' ', inner.ReadStringBound(i)), input, false).Length : (pending = ReadString(input)).Length;

        public int ReadStringExact(ReadOnlySpan<byte> input) => span ? Logged(i => new string(' ', inner.ReadStringExact(i)), input, false).Length : pending.Length;

        public int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination)
        {
            if (!span)
            {
                pending.CopyTo(destination);
                return pending.Length;
            }

            var room = new char[destination.Length];
            var written = 0;
            Logged(i => new string(room, 0, written = inner.ReadStringChars(i, room)), input, true);
            room.AsSpan(0, written).CopyTo(destination);
            return written;
        }

        // A string is logged with its characters, a failure with its type, message and position.
        private string Logged(ReadFunc read, ReadOnlySpan<byte> input, bool record)
        {
            try
            {
                var value = read(input);
                if (record)
                {
                    log.Add("s:" + value);
                }

                return value;
            }
            catch (Exception e) when (e is YUpdateException or OverflowException)
            {
                log.Add("e:" + e.GetType().Name + ":" + e.Message + ":" + inner.Reader.Remaining);
                throw;
            }
        }

        public void ResetDsCurVal() => inner.ResetDsCurVal();

        public long ReadDsClock(ReadOnlySpan<byte> input) => inner.ReadDsClock(input);

        public long ReadDsLength(ReadOnlySpan<byte> input) => inner.ReadDsLength(input);

        public ItemId ReadLeftId(ReadOnlySpan<byte> input) => inner.ReadLeftId(input);

        public ItemId ReadRightId(ReadOnlySpan<byte> input) => inner.ReadRightId(input);

        public long ReadClient(ReadOnlySpan<byte> input) => inner.ReadClient(input);

        public byte ReadInfo(ReadOnlySpan<byte> input) => inner.ReadInfo(input);

        public bool ReadParentInfo(ReadOnlySpan<byte> input) => inner.ReadParentInfo(input);

        public uint ReadTypeRef(ReadOnlySpan<byte> input) => inner.ReadTypeRef(input);

        public int ReadLength(ReadOnlySpan<byte> input) => inner.ReadLength(input);

        public object ReadAny(ReadOnlySpan<byte> input) => inner.ReadAny(input);

        public byte[] ReadBuffer(ReadOnlySpan<byte> input) => inner.ReadBuffer(input);

        public string ReadKey(ReadOnlySpan<byte> input) => inner.ReadKey(input);

        public object ReadJson(ReadOnlySpan<byte> input) => inner.ReadJson(input);
    }
}
