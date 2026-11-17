using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class ContentAnyTests
{
    [TestMethod]
    public void Splice_Decoded_Throws()
    {
        var content = new ContentAny(new object[] { 1L, 2L });

        Assert.ThrowsExactly<NotSupportedException>(() => content.Splice(1));
    }

    [TestMethod]
    public void MergeWith_Decoded_Throws()
    {
        var content = new ContentAny(new object[] { 1L });

        Assert.ThrowsExactly<NotSupportedException>(() => content.MergeWith(new ContentAny(new object[] { 2L })));
    }

    [TestMethod]
    public void Copy_Decoded_IsAnotherContentOfTheSameValues()
    {
        var content = new ContentAny(new object[] { "a", 2L });

        var copy = (ContentAny)content.Copy();

        Assert.AreEqual((false, "a", 2L), (ReferenceEquals(content, copy), copy.ValueAt(0), copy.ValueAt(1)));
    }

    [TestMethod]
    public void GetContent_Decoded_IsAReadOnlyViewOfTheValues()
    {
        var content = new ContentAny(new object[] { "a", 2L });

        var view = content.GetContent();

        Assert.AreEqual((2, "a", 2L), (view.Count, view[0], view[1]));
    }
}
