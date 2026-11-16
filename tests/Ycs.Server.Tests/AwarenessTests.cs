using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Server.Awareness;

[TestClass]
public class AwarenessTests
{
    private static readonly JsonElement Fixture = LoadFixture();

    private static JsonElement LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "awareness-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Steps => Fixture.GetProperty("steps").EnumerateArray();

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static ulong[] Ids(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetUInt64())];

    private static byte[] Raw(params (ulong Id, ulong Clock, string Json)[] entries)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)entries.Length);
        foreach (var (id, clock, json) in entries)
        {
            WriteVarUint(stream, id);
            WriteVarUint(stream, clock);
            var bytes = Encoding.UTF8.GetBytes(json);
            WriteVarUint(stream, (ulong)bytes.Length);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    private static void WriteVarUint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    // What an instance holds, in a form that compares: every client with its clock and state, in id order.
    private static string Snapshot(Awareness awareness, IEnumerable<ulong> known)
    {
        var sb = new StringBuilder();
        foreach (var id in known.Distinct().Order())
        {
            sb.Append(id).Append(':');
            sb.Append(awareness.TryGetClock(id, out var clock) ? clock.ToString() : "-").Append(':');
            sb.Append(awareness.TryGetState(id, out var state) ? state : "none").Append('\n');
        }

        return sb.ToString();
    }

    private static readonly ulong[] ProbeIds = [1, 2, 3, 7, 8, 9, 4294967295, 4294967296, 3735928559, (1UL << 53) - 1, (1UL << 40) + 5];

    private static string Describe(AwarenessChange c) =>
        $"+{string.Join(',', c.Added)} ~{string.Join(',', c.Updated)} !{string.Join(',', c.Changed)} -{string.Join(',', c.Removed)}";

    // y-protocols emits no event when nothing happened; its "change" event lists only the filtered updates.
    private static string Describe(JsonElement step)
    {
        var update = step.GetProperty("update");
        var change = step.GetProperty("change");
        string List(JsonElement e, string name) => e.ValueKind == JsonValueKind.Null ? string.Empty : string.Join(',', Ids(e.GetProperty(name)));
        return $"+{List(update, "added")} ~{List(update, "updated")} !{List(change, "updated")} -{List(update, "removed")}";
    }

    [TestMethod]
    public void ApplyUpdate_VectorsInSequence_MatchYProtocols()
    {
        var awareness = new Awareness(new AwarenessClock());
        var mismatches = new List<string>();

        foreach (var step in Steps)
        {
            var name = step.GetProperty("name").GetString();
            var change = step.GetProperty("op").GetString() == "apply"
                ? awareness.ApplyUpdate(Hex(step.GetProperty("input").GetString()!))
                : awareness.RemoveStates(Ids(step.GetProperty("ids")));

            if (Describe(change) != Describe(step))
            {
                mismatches.Add($"{name}: events {Describe(change)}, expected {Describe(step)}");
            }

            foreach (var client in step.GetProperty("after").GetProperty("clients").EnumerateArray())
            {
                var id = client.GetProperty("id").GetUInt64();
                var hasClock = awareness.TryGetClock(id, out var clock);
                var hasState = awareness.TryGetState(id, out var state);
                var present = client.GetProperty("present").GetBoolean();
                var sameState = hasState == present &&
                    (!present || JsonNode.DeepEquals(JsonNode.Parse(state), JsonNode.Parse(client.GetProperty("state").GetRawText())));
                if (!hasClock || clock != client.GetProperty("clock").GetUInt64() || !sameState)
                {
                    mismatches.Add($"{name}: client {id} holds clock {clock} state {state}");
                }
            }

            var presentIds = Ids(step.GetProperty("after").GetProperty("present"));
            if (!awareness.ClientIds.Order().SequenceEqual(presentIds.Order()))
            {
                mismatches.Add($"{name}: states of {string.Join(',', awareness.ClientIds)}");
            }

            var encodedAll = step.GetProperty("after").GetProperty("encodedAll");
            if (encodedAll.ValueKind == JsonValueKind.String && !(step.TryGetProperty("reformatted", out var reformatted) && reformatted.GetBoolean()) &&
                Convert.ToHexStringLower(awareness.EncodeUpdate(presentIds)) != encodedAll.GetString())
            {
                mismatches.Add($"{name}: encoding of all states differs");
            }

            if (step.GetProperty("op").GetString() == "remove")
            {
                var ids = Ids(step.GetProperty("encodedIds"));
                if (Convert.ToHexStringLower(awareness.EncodeUpdate(ids)) != step.GetProperty("encodedRemoved").GetString())
                {
                    mismatches.Add($"{name}: encoding of the removed differs");
                }
            }
        }

        Assert.AreEqual(string.Empty, string.Join('\n', mismatches));
    }

    [TestMethod]
    public void EncodeUpdate_ExplicitIds_MatchesYProtocolsInOrderAndWithNullStates()
    {
        var awareness = new Awareness(new AwarenessClock());
        foreach (var step in Steps)
        {
            if (step.GetProperty("op").GetString() == "apply")
            {
                awareness.ApplyUpdate(Hex(step.GetProperty("input").GetString()!));
            }
            else
            {
                awareness.RemoveStates(Ids(step.GetProperty("ids")));
            }
        }

        var actual = Fixture.GetProperty("encodes").EnumerateArray()
            .Select(e => Convert.ToHexStringLower(awareness.EncodeUpdate(Ids(e.GetProperty("ids")))));
        var expected = Fixture.GetProperty("encodes").EnumerateArray().Select(e => e.GetProperty("hex").GetString());

        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    [TestMethod]
    public void EncodeUpdate_FreshClientsOfOneMessage_ReproducesTheMessage()
    {
        var inputs = Steps
            .Where(s => s.GetProperty("op").GetString() == "apply" && s.GetProperty("update").ValueKind != JsonValueKind.Null &&
                        s.GetProperty("update").GetProperty("updated").GetArrayLength() == 0 &&
                        s.GetProperty("update").GetProperty("removed").GetArrayLength() == 0)
            .Select(s => s.GetProperty("input").GetString()!)
            .ToList();
        Assert.IsGreaterThanOrEqualTo(4, inputs.Count);

        var reencoded = inputs.Select(input =>
        {
            var awareness = new Awareness(new AwarenessClock());
            var change = awareness.ApplyUpdate(Hex(input));
            return Convert.ToHexStringLower(awareness.EncodeUpdate(change.Added));
        }).ToList();

        CollectionAssert.AreEqual(inputs, reencoded);
    }

    [TestMethod]
    public void EncodeUpdate_ReformattedState_KeepsTheTextAsReceived()
    {
        var awareness = new Awareness(new AwarenessClock());
        var message = Raw((5, 3, " { \"z\" : 1 ,\n \"é\":[1, 2] } "));
        awareness.ApplyUpdate(message);

        Assert.AreEqual(Convert.ToHexString(message), Convert.ToHexString(awareness.EncodeUpdate([5])));
    }

    [TestMethod]
    public void EncodeUpdate_UnknownClient_Throws()
    {
        var awareness = new Awareness(new AwarenessClock());

        Assert.ThrowsExactly<ArgumentException>(() => awareness.EncodeUpdate([1]));
    }

    [TestMethod]
    public void RemoveStates_AClientWithAState_KeepsItsClockAndEncodesNull()
    {
        var awareness = new Awareness(new AwarenessClock());
        awareness.ApplyUpdate(Raw((1, 7, "{\"a\":1}")));

        var change = awareness.RemoveStates([1, 2]);

        Assert.AreEqual(
            ("+ ~ ! -1", "010107046e756c6c", true, true),
            (Describe(change), Convert.ToHexStringLower(awareness.EncodeUpdate([1])), awareness.RemoveStates([1]).IsEmpty,
                awareness.ApplyUpdate(Raw((1, 7, "{\"a\":2}"))).IsEmpty));
    }

    [TestMethod]
    public void RemoveOutdated_WithAFakeClock_RemovesAfter30SecondsWithoutRenewal()
    {
        var clock = new AwarenessClock();
        var awareness = new Awareness(clock);
        var log = new List<string>();
        void Check() => log.Add(Describe(awareness.RemoveOutdated()));

        awareness.ApplyUpdate(Raw((1, 1, "{}")));
        clock.Advance(TimeSpan.FromSeconds(10));
        awareness.ApplyUpdate(Raw((2, 1, "{}"), (3, 1, "{}")));
        clock.Advance(TimeSpan.FromMilliseconds(19_999));
        Check();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check();
        awareness.ApplyUpdate(Raw((2, 2, "{}")));
        clock.Advance(TimeSpan.FromSeconds(29));
        Check();
        clock.Advance(TimeSpan.FromSeconds(1));
        Check();
        Check();

        Assert.AreEqual("+ ~ ! -|+ ~ ! -1|+ ~ ! -3|+ ~ ! -2|+ ~ ! -", string.Join('|', log));
    }

    [TestMethod]
    public void RemoveOutdated_ARemovedClientReturningWithTheSameClock_StaysRemoved()
    {
        var clock = new AwarenessClock();
        var awareness = new Awareness(clock);
        awareness.ApplyUpdate(Raw((1, 4, "{}")));
        clock.Advance(TimeSpan.FromSeconds(30));
        awareness.RemoveOutdated();

        var same = awareness.ApplyUpdate(Raw((1, 4, "{}")));
        var higher = awareness.ApplyUpdate(Raw((1, 5, "{}")));

        Assert.AreEqual("+ ~ ! -|+ ~1 !1 -", $"{Describe(same)}|{Describe(higher)}");
    }

    [TestMethod]
    public void ApplyUpdate_SameClockAgain_DoesNotRenewTheTimeout()
    {
        var clock = new AwarenessClock();
        var awareness = new Awareness(clock);
        awareness.ApplyUpdate(Raw((1, 1, "{}")));
        clock.Advance(TimeSpan.FromSeconds(20));
        awareness.ApplyUpdate(Raw((1, 1, "{}")));
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.AreEqual(1, awareness.RemoveOutdated().Removed.Count);
    }

    [TestMethod]
    public void ApplyUpdate_ObjectsWithOtherKeyOrderAndNumberSpelling_AreUpdatedNotChanged()
    {
        var awareness = new Awareness(new AwarenessClock());
        awareness.ApplyUpdate(Raw((1, 1, "{\"a\":1,\"b\":[true,null,\"x\"]}")));

        var change = awareness.ApplyUpdate(Raw((1, 2, "{\"b\":[true,null,\"x\"],\"a\":1.0}")));
        var differs = awareness.ApplyUpdate(Raw((1, 3, "{\"b\":[true,null,\"x\"],\"a\":2}")));
        var kinds = awareness.ApplyUpdate(Raw((1, 4, "{\"b\":[false,null,\"x\"],\"a\":2}")));

        Assert.AreEqual("+ ~1 ! -|+ ~1 !1 -|+ ~1 !1 -", $"{Describe(change)}|{Describe(differs)}|{Describe(kinds)}");
    }

    [TestMethod]
    public void ApplyUpdate_EveryTruncationOfEveryVector_ThrowsAndChangesNothing()
    {
        var awareness = new Awareness(new AwarenessClock());
        awareness.ApplyUpdate(Raw((1, 1, "{\"keep\":1}"), (4294967296, 2, "{\"keep\":2}")));
        var before = Snapshot(awareness, ProbeIds);
        var failures = new List<string>();

        foreach (var step in Steps.Where(s => s.GetProperty("op").GetString() == "apply"))
        {
            var input = Hex(step.GetProperty("input").GetString()!);
            for (var length = 0; length < input.Length; length++)
            {
                try
                {
                    awareness.ApplyUpdate(input.AsSpan(0, length));
                    failures.Add($"{step.GetProperty("name")} cut at {length} did not throw");
                }
                catch (YUpdateException)
                {
                }

                if (Snapshot(awareness, ProbeIds) != before)
                {
                    failures.Add($"{step.GetProperty("name")} cut at {length} changed the state");
                }
            }
        }

        Assert.AreEqual(string.Empty, string.Join('\n', failures));
    }

    [TestMethod]
    public void ApplyUpdate_ALaterEntryIsMalformed_AppliesNoEntry()
    {
        var awareness = new Awareness(new AwarenessClock());
        var bad = Raw((1, 1, "{}"), (2, 1, "{"));

        Assert.ThrowsExactly<YUpdateException>(() => awareness.ApplyUpdate(bad));
        Assert.IsFalse(awareness.TryGetClock(1, out _));
    }

    [TestMethod]
    [DataRow(new byte[] { 1, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 1, 2, (byte)'{', (byte)'}' }, DisplayName = "a client id of nine bytes")]
    [DataRow(new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, (byte)'{', (byte)'}' }, DisplayName = "a client id of 2^56 - 1")]
    [DataRow(new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x10, 1, 2, (byte)'{', (byte)'}' }, DisplayName = "a client id above 2^53 - 1")]
    [DataRow(new byte[] { 1, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 2, (byte)'{', (byte)'}' }, DisplayName = "a clock above 2^53 - 1")]
    [DataRow(new byte[] { 1, 1, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, (byte)'{', (byte)'}' }, DisplayName = "a string length of 2^32 - 1")]
    [DataRow(new byte[] { 1, 1, 1, 3, (byte)'{', (byte)'}' }, DisplayName = "a string longer than the message")]
    [DataRow(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, DisplayName = "a count with no entries")]
    [DataRow(new byte[] { 0x80 }, DisplayName = "a count that never ends")]
    [DataRow(new byte[] { 1, 1, 1, 2, 0xC3, 0x28 }, DisplayName = "invalid UTF-8")]
    [DataRow(new byte[] { 1, 1, 1, 5, 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' }, DisplayName = "a byte order mark")]
    public void ApplyUpdate_MalformedBytes_ThrowsYUpdateException(byte[] message)
    {
        var awareness = new Awareness(new AwarenessClock());

        Assert.ThrowsExactly<YUpdateException>(() => awareness.ApplyUpdate(message));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{")]
    [DataRow("{\"a\":1}x")]
    [DataRow("nul")]
    [DataRow("undefined")]
    [DataRow("{'a':1}")]
    [DataRow("[1,]")]
    [DataRow("NaN")]
    [DataRow("\"unterminated")]
    public void ApplyUpdate_InvalidJson_ThrowsYUpdateExceptionAndChangesNothing(string json)
    {
        var awareness = new Awareness(new AwarenessClock());
        awareness.ApplyUpdate(Raw((1, 1, "{}")));
        var before = Snapshot(awareness, ProbeIds);

        Assert.ThrowsExactly<YUpdateException>(() => awareness.ApplyUpdate(Raw((1, 2, "{\"ok\":1}"), (2, 1, json))));
        Assert.AreEqual(before, Snapshot(awareness, ProbeIds));
    }

    [TestMethod]
    public void ApplyUpdate_MaxSafeIntegerClock_IsAcceptedAndNothingNewerIs()
    {
        var awareness = new Awareness(new AwarenessClock());

        var change = awareness.ApplyUpdate(Raw((1, (1UL << 53) - 1, "{}")));

        Assert.AreEqual(1, change.Added.Count);
    }
}
