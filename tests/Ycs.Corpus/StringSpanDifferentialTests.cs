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
            var cursor = new EncodingUtils.StructCursor(new Logging(decoder, span, log), new StagingTable());
            while (cursor.NextStaged() >= 0)
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

    /// <summary>Delegates to the decoder; a string is read with <see cref="IUpdateDecoder.ReadStringChars"/> or <see cref="IUpdateDecoder.ReadString"/> and logged, a failure with its position.</summary>
    private sealed class Logging(IUpdateDecoder inner, bool span, List<string> log) : IUpdateDecoder
    {
        public Lib0Reader Reader => inner.Reader;

        public long MaxStructs => inner.MaxStructs;

        public string ReadString() => Logged(inner.ReadString, true);

        private string pending = "";

        // The reference reads the whole string here; the split path only the bound, and logs the characters in ReadStringChars.
        public int ReadStringBound() => span ? Logged(() => new string(' ', inner.ReadStringBound()), false).Length : (pending = ReadString()).Length;

        public int ReadStringExact() => span ? Logged(() => new string(' ', inner.ReadStringExact()), false).Length : pending.Length;

        public int ReadStringChars(Span<char> destination)
        {
            if (!span)
            {
                pending.CopyTo(destination);
                return pending.Length;
            }

            var room = new char[destination.Length];
            var written = 0;
            Logged(() => new string(room, 0, written = inner.ReadStringChars(room)), true);
            room.AsSpan(0, written).CopyTo(destination);
            return written;
        }

        // A string is logged with its characters, a failure with its type, message and position.
        private string Logged(Func<string> read, bool record)
        {
            try
            {
                var value = read();
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

        public long ReadDsClock() => inner.ReadDsClock();

        public long ReadDsLength() => inner.ReadDsLength();

        public ItemId ReadLeftId() => inner.ReadLeftId();

        public ItemId ReadRightId() => inner.ReadRightId();

        public long ReadClient() => inner.ReadClient();

        public byte ReadInfo() => inner.ReadInfo();

        public bool ReadParentInfo() => inner.ReadParentInfo();

        public uint ReadTypeRef() => inner.ReadTypeRef();

        public int ReadLength() => inner.ReadLength();

        public object ReadAny() => inner.ReadAny();

        public byte[] ReadBuffer() => inner.ReadBuffer();

        public string ReadKey() => inner.ReadKey();

        public object ReadJson() => inner.ReadJson();
    }
}
