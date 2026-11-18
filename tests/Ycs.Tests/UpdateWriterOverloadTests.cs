using System;
using System.Buffers;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The <see cref="IBufferWriter{T}"/> forms of <see cref="YUpdate"/>.</summary>
[TestClass]
public class UpdateWriterOverloadTests
{
    private static readonly byte[] Hostile = [200, 200];

    private static byte[] Encoded(int clientId, string text, UpdateFormat format)
    {
        var doc = new YDoc(new YDocOptions { ClientId = clientId, GarbageCollection = false });
        doc.GetText("t").Insert(0, text);
        doc.GetText("t").Remove(0, 1);
        return doc.EncodeStateAsUpdate(format: format);
    }

    // Each operation as its array form and as its writer form, over the same inputs.
    private static (string Name, Func<byte[]> Array, Action<IBufferWriter<byte>> Writer)[] Operations(UpdateFormat format)
    {
        var a = Encoded(1, "hello", format);
        var b = Encoded(2, "world", format);
        var other = format == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;
        var vector = YUpdate.EncodeStateVector(a, format);
        return
        [
            ("Merge", () => YUpdate.Merge([a, b], format), w => YUpdate.Merge(w, [a, b], format)),
            ("Merge1", () => YUpdate.Merge([a], format), w => YUpdate.Merge(w, [a], format)),
            ("Diff", () => YUpdate.Diff(b, vector, format), w => YUpdate.Diff(w, b, vector, format)),
            ("Convert", () => YUpdate.Convert(a, format, other), w => YUpdate.Convert(w, a, format, other)),
            ("ConvertSame", () => YUpdate.Convert(a, format, format), w => YUpdate.Convert(w, a, format, format)),
            ("EncodeStateVector", () => YUpdate.EncodeStateVector(a, format), w => YUpdate.EncodeStateVector(w, a, format)),
        ];
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void WriterForms_WriteTheBytesOfTheArrayForms(UpdateFormat format)
    {
        var actual = Operations(format).Select(o =>
        {
            var writer = new ArrayBufferWriter<byte>();
            o.Writer(writer);
            return (o.Name, Convert.ToHexString(writer.WrittenSpan));
        });

        var expected = Operations(format).Select(o => (o.Name, Convert.ToHexString(o.Array())));
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void WriterForms_HostileInput_ThrowAndWriteNothing(UpdateFormat format)
    {
        var good = Encoded(1, "hello", format);
        var vector = YUpdate.EncodeStateVector(good, format);
        var writer = new ArrayBufferWriter<byte>();
        Action[] calls =
        [
            () => YUpdate.Merge(writer, [good, Hostile], format),
            () => YUpdate.Diff(writer, Hostile, vector, format),
            () => YUpdate.Convert(writer, Hostile, format, format),
            () => YUpdate.EncodeStateVector(writer, Hostile, format),
        ];

        var thrown = calls.Select(call =>
        {
            try
            {
                call();
                return "none";
            }
            catch (YUpdateException e)
            {
                return e.GetType().Name;
            }
        });

        Assert.AreEqual((string.Join(",", Enumerable.Repeat(nameof(YUpdateException), 4)), 0), (string.Join(",", thrown), writer.WrittenCount));
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void WriterForms_AfterAPrefix_KeepThePrefix(UpdateFormat format)
    {
        byte[] prefix = [1, 2, 3];
        var actual = Operations(format).Select(o =>
        {
            var writer = new ArrayBufferWriter<byte>();
            writer.Write(prefix);
            o.Writer(writer);
            return (o.Name, Convert.ToHexString(writer.WrittenSpan));
        });

        var expected = Operations(format).Select(o => (o.Name, Convert.ToHexString([.. prefix, .. o.Array()])));
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }
}
