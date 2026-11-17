using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Relay redelivery (the Plenum relay's sequences, Fixtures/relay-redelivery.json): the updates of two peers with gc on,
/// delivered to a fresh document as a relay does. A join's state followed by the stored updates it holds already, in
/// order and out of order; owed updates resent twice, out of order; one peer's updates in reverse to a document that
/// holds the other's. Each ends in the text and the state Yjs 13.6.32 ends in.
/// </summary>
[TestClass]
public class RelayRedeliveryTests
{
    // The v1 state Yjs 13.6.32 ends in: the same for the three sequences.
    private const string YjsState = "0201020084010A072C206272617665040100040107636F6E74656E740568656C6C6F810104064101000284020601210101010508";

    private static readonly Lazy<JsonElement> Sequences = new(() =>
        JsonElement.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay-redelivery.json"))));

    public static IEnumerable<object[]> Names => Sequences.Value.EnumerateObject().Select(p => new object[] { p.Name });

    [TestMethod]
    [DynamicData(nameof(Names))]
    public void ApplyUpdate_RelayRedeliverySequence_EndsAsYjsDoes(string name)
    {
        var sequence = Sequences.Value.GetProperty(name);
        var doc = new YDoc();

        foreach (var update in sequence.GetProperty("updates").EnumerateArray())
        {
            doc.ApplyUpdate(Convert.FromHexString(update.GetString()!));
        }

        Assert.AreEqual(
            (sequence.GetProperty("text").GetString(), YjsState, false),
            (doc.GetText("content").ToString(), Convert.ToHexString(doc.EncodeStateAsUpdate()), doc.HasPending),
            name);
    }
}
