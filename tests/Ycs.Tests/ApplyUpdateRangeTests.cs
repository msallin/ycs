using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The overloads of <see cref="YDoc"/> that read a range of an array in place.</summary>
[TestClass]
public class ApplyUpdateRangeTests
{
    private const string Secret = "SECRET-DOCUMENT-CONTENT";

    private static byte[] SecretUpdate(UpdateFormat format = UpdateFormat.V1)
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, Secret);
        return doc.EncodeStateAsUpdate(format: format);
    }

    [TestMethod]
    public void ApplyUpdate_FromARangeOfAnArray_AppliesOnlyThatRange()
    {
        var update = SecretUpdate();
        var message = new byte[update.Length + 3];
        message[0] = 7;
        update.CopyTo(message, 1);
        message[^1] = 9;
        var doc = new YDoc();

        doc.ApplyUpdate(message, 1, update.Length);

        Assert.AreEqual(Secret, doc.GetText("t").ToString());
    }

    [TestMethod]
    public void ApplyUpdate_FromARangeInV2_AppliesIt()
    {
        var update = SecretUpdate(UpdateFormat.V2);
        var message = new byte[update.Length + 1];
        update.CopyTo(message, 1);
        var doc = new YDoc();

        doc.ApplyUpdate(message, 1, update.Length, UpdateFormat.V2);

        Assert.AreEqual(Secret, doc.GetText("t").ToString());
    }

    [TestMethod]
    public void TryApplyUpdate_FromARange_ReportsMalformedAndWellFormed()
    {
        var update = SecretUpdate();
        var message = new byte[update.Length + 1];
        update.CopyTo(message, 1);

        var results = (
            new YDoc().TryApplyUpdate(message, 1, update.Length),
            new YDoc().TryApplyUpdate(message, 0, update.Length, UpdateFormat.V2),
            new YDoc().TryApplyUpdate(message, 1, update.Length, UpdateFormat.V1));

        Assert.AreEqual((true, false, true), results);
    }

    [TestMethod]
    [DataRow(-1, 1)]
    [DataRow(0, -1)]
    [DataRow(2, 4)]
    [DataRow(5, 0)]
    public void ApplyUpdate_RangeOutsideTheArray_Throws(int offset, int count)
    {
        var doc = new YDoc();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.ApplyUpdate(new byte[4], offset, count));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.TryApplyUpdate(new byte[4], offset, count, UpdateFormat.V2));
    }
}
