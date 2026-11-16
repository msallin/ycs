using Ycs.Client.Tests.Support;
using Ycs.Server.Awareness;

namespace Ycs.Client.Tests;

[TestClass]
public class ClientAwarenessTests
{
    [TestMethod]
    public void SetLocalState_Twice_TheServerTakesBothWithRisingClocks()
    {
        var client = new ClientAwareness(7, new ManualClock());
        var server = new Awareness(new ManualClock());

        server.ApplyUpdate(Payload(client.SetLocalState("""{"a":1}""")));
        server.ApplyUpdate(Payload(client.SetLocalState("""{"a":2}""")));

        server.TryGetState(7, out var state);
        server.TryGetClock(7, out var clock);
        Assert.AreEqual(("""{"a":2}""", 2UL), (state, clock));
    }

    [TestMethod]
    public void SetLocalState_Null_RemovesTheStateAtTheServer()
    {
        var client = new ClientAwareness(7, new ManualClock());
        var server = new Awareness(new ManualClock());
        server.ApplyUpdate(Payload(client.SetLocalState("{}")));

        var change = server.ApplyUpdate(Payload(client.SetLocalState(null)));

        CollectionAssert.AreEqual(new ulong[] { 7 }, change.Removed.ToArray());
    }

    [TestMethod]
    public void RenewIfDue_HalfTheTimeout_SendsTheStateAgainWithTheNextClock()
    {
        var clock = new ManualClock();
        var client = new ClientAwareness(7, clock);
        client.SetLocalState("{}");
        clock.Advance(ClientAwareness.RenewAfter - TimeSpan.FromMilliseconds(1));
        var early = client.RenewIfDue();
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var due = client.RenewIfDue();

        Assert.IsNull(early);
        CollectionAssert.AreEqual(Wire.AwarenessMessage(7, 2, "{}"), due);
    }

    [TestMethod]
    public void RenewIfDue_NoState_SendsNothing()
    {
        var clock = new ManualClock();
        var client = new ClientAwareness(7, clock);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.IsNull(client.RenewIfDue());
    }

    [TestMethod]
    public void ApplyUpdate_OwnStateRemovedByTheServer_IsSentAgainWithANewerClock()
    {
        var client = new ClientAwareness(7, new ManualClock());
        client.SetLocalState("{}");
        client.SetLocalState("{}");

        var (_, reply) = client.ApplyUpdate(Payload(Wire.AwarenessMessage(7, 2, null)));

        CollectionAssert.AreEqual(Wire.AwarenessMessage(7, 3, "{}"), reply);
    }

    [TestMethod]
    public void ApplyUpdate_EchoOfTheOwnState_IsNoRemoteState()
    {
        var client = new ClientAwareness(7, new ManualClock());
        var echo = client.SetLocalState("{}");

        var (change, reply) = client.ApplyUpdate(Payload(echo));

        Assert.AreEqual((1, 0, (byte[]?)null), (change.Added.Count, client.Remote.Count(), reply));
    }

    [TestMethod]
    public void ApplyUpdate_OtherClient_IsARemoteState()
    {
        var client = new ClientAwareness(7, new ManualClock());

        client.ApplyUpdate(Payload(Wire.AwarenessMessage(8, 1, """{"x":true}""")));

        CollectionAssert.AreEqual(new[] { (8UL, """{"x":true}""") }, client.Remote.ToArray());
    }

    [TestMethod]
    public void Forget_AfterwardsTheSameClock_IsTakenAgain()
    {
        var client = new ClientAwareness(7, new ManualClock());
        var other = Payload(Wire.AwarenessMessage(8, 5, "{}"));
        client.ApplyUpdate(other);

        var forgotten = client.Forget();
        client.ApplyUpdate(other);

        Assert.AreEqual((1, 1), (forgotten.Removed.Count, client.Remote.Count()));
    }

    [TestMethod]
    public void RemoveOutdated_RemoteNotRenewedFor30Seconds_IsRemoved()
    {
        var clock = new ManualClock();
        var client = new ClientAwareness(7, clock);
        client.ApplyUpdate(Payload(Wire.AwarenessMessage(8, 1, "{}")));
        clock.Advance(TimeSpan.FromMilliseconds(Awareness.OutdatedTimeoutMs));

        var change = client.RemoveOutdated();

        Assert.AreEqual((1, 0), (change.Removed.Count, client.Remote.Count()));
    }

    // The awareness update inside an awareness message: after the type, the length-prefixed payload.
    private static byte[] Payload(byte[] message)
    {
        var reader = new MemoryStream(message);
        Assert.AreEqual(Wire.Awareness, Wire.ReadVarUint(reader));
        return Wire.ReadVarUint8Array(reader);
    }
}
