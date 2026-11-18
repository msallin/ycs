using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>A slice of a larger buffer is applied in place, for both formats and both entry points.</summary>
[TestClass]
public class ApplyUpdateRangeTests
{
    private const string Secret = "SECRET-DOCUMENT-CONTENT";

    private static byte[] SecretUpdate(UpdateFormat format)
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, Secret);
        return doc.EncodeStateAsUpdate(format: format);
    }

    private static byte[] WithHeaderAndTrailer(byte[] update)
    {
        var message = new byte[update.Length + 2];
        message[0] = 7;
        update.CopyTo(message, 1);
        message[^1] = 9;
        return message;
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void ApplyUpdate_FromASliceBehindAHeader_AppliesOnlyTheSlice(UpdateFormat format)
    {
        var update = SecretUpdate(format);
        var message = WithHeaderAndTrailer(update);
        var doc = new YDoc();

        doc.ApplyUpdate(message.AsSpan(1, update.Length), format);

        Assert.AreEqual(Secret, doc.GetText("t").ToString());
    }

    [TestMethod]
    public void TryApplyUpdate_FromASlice_ReportsMalformedAndWellFormed()
    {
        var update = SecretUpdate(UpdateFormat.V1);
        var message = WithHeaderAndTrailer(update);

        var results = (
            new YDoc().TryApplyUpdate(message.AsSpan(1, update.Length)),
            new YDoc().TryApplyUpdate(message.AsSpan(0, update.Length), UpdateFormat.V2),
            new YDoc().TryApplyUpdate(message.AsSpan(1, update.Length), UpdateFormat.V1));

        Assert.AreEqual((true, false, true), results);
    }
}
