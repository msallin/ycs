using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class RelativePositionTests : YTestBase
{
    [TestMethod]
    public void TestRelativePositionCase1()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "1");
        yText.Insert(0, "abc");
        yText.Insert(0, "z");
        yText.Insert(0, "y");
        yText.Insert(0, "x");

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionCase2()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "abc");

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionCase3()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "abc");
        yText.Insert(0, "1");
        yText.Insert(0, "xyz");

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionCase4()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "1");

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionCase5()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "2");
        yText.Insert(0, "1");

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionCase6()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        CheckRelativePositions(yText);
    }

    [TestMethod]
    public void TestRelativePositionAssociationDifference()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];

        yText.Insert(0, "2");
        yText.Insert(0, "1");

        var rposRight = RelativePosition.FromIndex(yText, 1, Assoc.After);
        var rposLeft = RelativePosition.FromIndex(yText, 1, Assoc.Before);

        yText.Insert(1, "x");

        Assert.IsTrue(rposRight.TryResolve(Users[0], out var posRight));
        Assert.IsTrue(rposLeft.TryResolve(Users[0], out var posLeft));
        Assert.AreEqual(2, posRight.Index);
        Assert.AreEqual(1, posLeft.Index);
    }

    [TestMethod]
    public void Decode_InputOverwrittenAfterwards_ResultUnchanged()
    {
        var doc = new YDoc();
        var text = doc.GetText("root");
        text.Insert(0, "abc");
        var positions = new[] { RelativePosition.FromIndex(text, 1, Assoc.After), RelativePosition.FromIndex(text, 3, Assoc.Before) };

        var survived = positions.Select(p =>
        {
            var input = p.Encode();
            var decoded = RelativePosition.Decode(input);
            input.AsSpan().Fill(0xFF);
            return decoded.Equals(p);
        }).ToArray();

        CollectionAssert.AreEqual(new[] { true, true }, survived);
    }

    private static void CheckRelativePositions(YText yText)
    {
        // Test if all positions are encoded and restored correctly.
        for (int i = 0; i < yText.Length; i++)
        {
            // For all types of assotiations.
            foreach (var assoc in new[] { Assoc.Before, Assoc.After })
            {
                var rpos = RelativePosition.FromIndex(yText, i, assoc);
                Assert.AreEqual(rpos, RelativePosition.Decode(rpos.Encode()));
                var encodedRpos = rpos.Encode();
                var decodedRpos = RelativePosition.Decode(encodedRpos);
                Assert.IsTrue(decodedRpos.TryResolve(yText.Doc, out var absPos));

                Assert.AreEqual(i, absPos.Index);
                Assert.AreEqual(assoc, absPos.Assoc);
            }
        }
    }

    [TestMethod]
    [DataRow(0, Assoc.After)]
    [DataRow(0, Assoc.Before)]
    [DataRow(2, Assoc.After)]
    [DataRow(2, Assoc.Before)]
    [DataRow(5, Assoc.After)]
    [DataRow(5, Assoc.Before)]
    public void FromIndex_DecodeOfEncode_IsEqual(int index, Assoc assoc)
    {
        Init(users: 1);
        var yText = Texts[Users[0]];
        yText.Insert(0, "hello");
        var rpos = RelativePosition.FromIndex(yText, index, assoc);

        Assert.AreEqual(rpos, RelativePosition.Decode(rpos.Encode()));
    }

    [TestMethod]
    public void ToJsonString_FromJsonString_RoundTrips()
    {
        Init(users: 1);
        var yText = Texts[Users[0]];
        yText.Insert(0, "hello");
        var rpos = RelativePosition.FromIndex(yText, 2, Assoc.Before);

        Assert.AreEqual((rpos, "{\"item\":{\"client\":" + rpos.Item!.Value.Client + ",\"clock\":" + (rpos.Item!.Value.Clock) + "},\"assoc\":-1}"),
            (RelativePosition.FromJson(rpos.ToJsonString()), rpos.ToJsonString()));
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("{\"assoc\":0}")]
    public void FromJson_NotAPosition_ThrowsYUpdateException(string json)
    {
        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.FromJson(json));
    }

    [TestMethod]
    public void FromIndex_TypeOutsideADocument_ThrowsArgumentException()
    {
        var e = Assert.ThrowsExactly<ArgumentException>(() => RelativePosition.FromIndex(new YText("a"), 0));

        Assert.AreEqual("The type is not part of a document. (Parameter 'type')", e.Message);
    }

    [TestMethod]
    public void TryResolve_AfterUndoAndARemoteSplitOfTheUndoneItem_ResolvesToTheSameCharacter()
    {
        var a = new YDoc();
        var b = new YDoc();
        var ta = a.GetText("t");
        var tb = b.GetText("t");
        var undoManager = new UndoManager(ta);
        ta.Insert(0, "abc");
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var relative = RelativePosition.FromIndex(ta, 2);

        undoManager.StopCapturing();
        ta.Remove(0, 3);
        undoManager.Undo();
        tb.Insert(1, "x");
        a.ApplyUpdate(b.EncodeStateAsUpdate(a.EncodeStateVector()));

        Assert.IsTrue(relative.TryResolve(a, out var absolute));
        var text = ta.ToString();
        Assert.AreEqual('c', text[absolute.Index], $"text '{text}', index {absolute.Index}");
    }
}
