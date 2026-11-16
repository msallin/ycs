using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// An embed whose value is null (written by another peer as the JSON "null") is an insert of length 1
/// in a delta, as Yjs' <c>{ insert: null }</c>: a <see cref="Delta.Insert"/> of <see cref="YValue.Null"/>.
/// </summary>
[TestClass]
public class NullEmbedTests
{
    // Client 7: an embed (content 5) without origins in the root text "t", whose JSON is "null".
    private static readonly byte[] NullEmbedUpdate = Convert.FromHexString("01010700050101740" + "46E756C6C00");

    [TestMethod]
    public void ToDelta_EmbedOfNull_IsTheNullValue()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(NullEmbedUpdate);

        var delta = doc.GetText("t").ToDelta();

        Assert.AreEqual(YValue.Null, delta[0].Inserted());
    }

    [TestMethod]
    public void EventDelta_EmbedOfNull_IsTheNullValue()
    {
        var doc = new YDoc();
        YValue? insert = null;
        doc.GetText("t").Observe(e => insert = e.Delta[0].Inserted());

        doc.ApplyUpdate(NullEmbedUpdate);

        Assert.AreEqual(YValue.Null, insert);
    }

    [TestMethod]
    public void ApplyDelta_NullEmbed_InsertsTheEmbedOfNull()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 7 });

        doc.GetText("t").ApplyDelta(new List<Delta> { new Delta.Insert(YValue.Null) });

        Assert.AreEqual(Convert.ToHexString(NullEmbedUpdate), Convert.ToHexString(doc.EncodeStateAsUpdate()));
    }
}
