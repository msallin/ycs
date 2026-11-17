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
}
