using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class RootPlaceholderTests
{
    [TestMethod]
    public void UndefinedRootAsYTypeIsAPlaceholderTheDefinitionReplaces()
    {
        var doc = new YDoc();

        var placeholder = doc.Get<YType>("a");
        var array = doc.GetArray("a");

        Assert.IsTrue(placeholder is not YArray && array.Doc == doc && !ReferenceEquals(placeholder, array));
        Assert.Throws<InvalidOperationException>(() => placeholder.ToJson());
    }

    [TestMethod]
    public void KindThatIsNotARootTypeThrows()
    {
        var doc = new YDoc();

        Assert.Throws<InvalidOperationException>(() => doc.Get<YXmlHook>("h"));
    }
}
