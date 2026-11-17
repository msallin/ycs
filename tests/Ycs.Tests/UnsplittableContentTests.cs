using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class UnsplittableContentTests
{
    [TestMethod]
    public void Splice_ContentOfLengthOne_Throws()
    {
        IContent[] contents =
        [
            new ContentBinary([1]),
            new ContentEmbed("e"),
            new ContentFormat("bold", true),
            new ContentDoc(new YDoc()),
        ];

        foreach (var content in contents)
        {
            Assert.AreEqual(1, content.Length);
            Assert.ThrowsExactly<InvalidOperationException>(() => content.Splice(1));
        }
    }

    [TestMethod]
    public void InternalCopy_UndefinedRootType_Throws()
    {
        var placeholder = new YTypePlaceholder();

        Assert.ThrowsExactly<InvalidOperationException>(() => placeholder.InternalCopy());
        Assert.ThrowsExactly<InvalidOperationException>(() => placeholder.InternalClone());
        Assert.ThrowsExactly<InvalidOperationException>(() => placeholder.Write(null!));
    }
}
