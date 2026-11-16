namespace Ycs.Client.Tests;

[TestClass]
public class PresenceTests
{
    [TestMethod]
    public void State_NameAndColour_ParsesBack()
    {
        var state = Presence.State("ann \"a\"", "#aa00bb");

        Assert.AreEqual(new PresenceUser(3, "ann \"a\"", "#aa00bb"), Presence.Parse(3, state));
    }

    [TestMethod]
    public void GuestState_Always_IsAUser()
    {
        var user = Presence.Parse(1, Presence.GuestState());

        Assert.IsTrue(user is { Name.Length: 10, Colour.Length: 7 });
    }

    // What y-codemirror's and y-quill's demos set: more members, in the state and in user, are ignored.
    [TestMethod]
    public void Parse_EditorBindingsUser_IsTheUserWithTheColourInLowerCase()
    {
        const string json = """{"user":{"name":"bob","color":"#30BCED","colorLight":"#30bced33"},"cursor":null}""";

        Assert.AreEqual(new PresenceUser(9, "bob", "#30bced"), Presence.Parse(9, json));
    }

    [TestMethod]
    [DataRow("""{}""")]
    [DataRow("""{"user":"bob"}""")]
    [DataRow("""{"user":{"name":"bob"}}""")]
    [DataRow("""{"user":{"name":1,"color":"#000000"}}""")]
    [DataRow("""{"user":{"name":"bob","color":"red"}}""")]
    [DataRow("""{"user":{"name":"bob","color":"#00000g"}}""")]
    [DataRow("""{"user":{"name":"bob","color":"#0000000"}}""")]
    [DataRow("""{"user":{"name":"\ud800","color":"#000000"}}""")]
    [DataRow("""[1]""")]
    [DataRow("""not json""")]
    public void Parse_NotAUser_IsNull(string json) => Assert.IsNull(Presence.Parse(1, json));

    [TestMethod]
    [DataRow(50, true)]
    [DataRow(51, false)]
    public void Parse_Name_IsTakenUpTo50CodeUnits(int length, bool taken) =>
        Assert.AreEqual(taken, Presence.Parse(1, Presence.State(new string('n', length), "#000000")) is not null);

    [TestMethod]
    public void Users_SomeStatesNameNoUser_AreLeftOut()
    {
        (ulong, string)[] states = [(1, Presence.State("a", "#000001")), (2, "{}"), (3, Presence.State("c", "#000003"))];

        CollectionAssert.AreEqual(
            new[] { new PresenceUser(1, "a", "#000001"), new PresenceUser(3, "c", "#000003") },
            Presence.Users(states).ToArray());
    }
}
