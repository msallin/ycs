using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Minimal reproductions of the divergences from Yjs the corpus found. The updates are real
/// Yjs 13.6.32 output; the expected values are what Yjs does with them. See KnownFailures.
/// </summary>
[TestClass]
public class DivergenceTests
{
    // Client 1: getText('other').insert(0, 'a'), then getText('text').insert(0, 'b').
    private const string InsertOther = "AQEBAAQBBW90aGVyAWEA";
    private const string InsertText = "AQEBAQQBBHRleHQBYgA=";

    [TestMethod]
    public void ApplyV1_RootRealizedWhilePending_PendingItemLandsInText() => Run(() =>
    {
        var doc = CorpusDoc.Create();
        doc.ApplyV1(Convert.FromBase64String(InsertText)); // clock 1 before clock 0: pending
        _ = doc.Text("text");                               // realizes the root as Y.Text
        doc.ApplyV1(Convert.FromBase64String(InsertOther));
        Assert.AreEqual("b", doc.Text("text"));
    });

    // From text-out-of-order-duplicated: client 5 inserts after an item of client 6 (5:24, origin
    // 6:14); client 7 types 'g' into the empty text (7:0, no dependencies).
    private const string DependsOnMissingClient = "AQEFGMQGDgYPAUEA";
    private const string Independent = "AQEHAAQBBHRleHQBZwA=";

    [TestMethod]
    public void ApplyV1_PendingStructOfAnotherClient_IndependentUpdateStillIntegrates() => Run(() =>
    {
        var doc = CorpusDoc.Create();
        doc.ApplyV1(Convert.FromBase64String(DependsOnMissingClient)); // pending: client 6 is unknown
        doc.ApplyV1(Convert.FromBase64String(Independent));
        Assert.AreEqual("g", doc.Text("text"));
    });

    // Clients 1 and 2 each insert one character into an empty text; client 3 then deletes both.
    private const string Client1Insert = "AQEBAAQBBHRleHQBYQA=";
    private const string Client2Insert = "AQECAAQBBHRleHQBYgA=";
    private const string Client3DeletesBoth = "AAICAQABAQEAAQ==";

    [TestMethod]
    public void EncodeStateVector_TwoClients_HighestClientFirstLikeYjs() => Run(() =>
    {
        var doc = CorpusDoc.Create();
        doc.ApplyV1(Convert.FromBase64String(Client1Insert));
        doc.ApplyV1(Convert.FromBase64String(Client2Insert));
        Assert.AreEqual("AgIBAQE=", Convert.ToBase64String(doc.EncodeStateVector()));
    });

    [TestMethod]
    public void EncodeStateV1_DeleteSetOfTwoClients_HighestClientFirstLikeYjs() => Run(() =>
    {
        var doc = CorpusDoc.Create();
        doc.ApplyV1(Convert.FromBase64String(Client1Insert));
        doc.ApplyV1(Convert.FromBase64String(Client2Insert));
        doc.ApplyV1(Convert.FromBase64String(Client3DeletesBoth));
        Assert.AreEqual("AgECAAEBBHRleHQBAQEAAQEEdGV4dAECAgEAAQEBAAE=", Convert.ToBase64String(doc.EncodeStateV1()));
    });

    private static void Run(Action test, [System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        KnownFailures.Check(nameof(DivergenceTests), name, test);
}
