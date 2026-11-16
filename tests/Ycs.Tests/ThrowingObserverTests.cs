using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Observers and document handlers that throw, phase by phase of the cleanup, against what Yjs 13.6.32 does
/// (Fixtures/throwing-observers.json, interop/js/throwing-observers.mjs): which handlers are called, in which
/// order, which exception escapes, and what a later change of the document still calls. Where Yjs leaves the
/// document broken (a handler of BeforeAllTransactions, BeforeTransaction, AfterTransactionCleanup, Update,
/// UpdateV2 or SubdocsChanged threw), Ycs deliberately recovers (docs/api.md); those scenarios state both.
/// </summary>
[TestClass]
public class ThrowingObserverTests
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "throwing-observers.json"))).RootElement.GetProperty("scenarios"));

    private const string AllHandlers = "beforeAllTransactions beforeTransaction beforeObserverCalls obs:a obs:a2 obs:m obs:t deep:a deep:m " +
        "afterTransaction afterTransaction2 afterTransactionCleanup update updateV2 afterAllTransactions";

    private const string Later = "later: beforeAllTransactions beforeTransaction beforeObserverCalls obs:a obs:a2 deep:a afterTransaction " +
        "afterTransaction2 afterTransactionCleanup update updateV2 afterAllTransactions\nlater escaped: -\n";

    // An empty transaction's cleanup: no observer and no update event.
    private const string EmptyCleanup = "beforeObserverCalls afterTransaction afterTransaction2 afterTransactionCleanup afterAllTransactions";

    /// <summary>
    /// The scenarios in which Ycs deliberately differs from Yjs: Yjs' document is broken afterwards (the recorded
    /// <c>broken</c>), Ycs' finishes the cleanup, still lets the exception escape, and goes on as usual.
    /// </summary>
    private static readonly Dictionary<string, string> Recovers = new()
    {
        ["afterTransactionCleanup"] = $"log: {AllHandlers}\nescaped: afterTransactionCleanup\n{Later}content: [\"later\",1]\nbroken: False",
        ["update"] = $"log: {AllHandlers}\nescaped: update\n{Later}content: [\"later\",1]\nbroken: False",
        ["updateV2"] = $"log: {AllHandlers}\nescaped: updateV2\n{Later}content: [\"later\",1]\nbroken: False",
        ["beforeAllTransactions"] = $"log: beforeAllTransactions {EmptyCleanup}\nescaped: beforeAllTransactions\n{Later}content: [\"later\"]\nbroken: False",
        ["beforeTransaction"] = $"log: beforeAllTransactions beforeTransaction {EmptyCleanup}\nescaped: beforeTransaction\n{Later}content: [\"later\"]\nbroken: False",
        ["subdocs"] = "log: beforeAllTransactions beforeTransaction beforeObserverCalls obs:m deep:m afterTransaction afterTransaction2 " +
            $"afterTransactionCleanup update updateV2 subdocs afterAllTransactions\nescaped: subdocs\n{Later}content: [\"later\"]\nbroken: False",
    };

    public static IEnumerable<object[]> ParityScenarios =>
        Fixture.Value.EnumerateObject().Where(p => !Recovers.ContainsKey(p.Name)).Select(p => new object[] { p.Name });

    public static IEnumerable<object[]> DifferingScenarios => Recovers.Keys.Select(k => new object[] { k });

    [TestMethod]
    [DynamicData(nameof(ParityScenarios))]
    public void Scenario_AsYjs(string name)
    {
        var expected = Fixture.Value.GetProperty(name);

        var actual = Run(name);

        Assert.AreEqual(Render(expected), actual);
    }

    [TestMethod]
    [DynamicData(nameof(DifferingScenarios))]
    public void Scenario_YjsBreaksTheDocument_YcsRecovers(string name)
    {
        var yjs = Fixture.Value.GetProperty(name);

        var actual = Run(name);

        Assert.AreEqual($"Yjs: document broken\nYcs: recovers\n{Recovers[name]}", $"Yjs: {(yjs.GetProperty("broken").GetBoolean() ? "document broken" : "fine")}\nYcs: recovers\n{actual}");
    }

    [TestMethod]
    public void Fixture_EveryDifferenceIsAScenarioOfYjs()
    {
        var recorded = Fixture.Value.EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.IsTrue(Recovers.Keys.All(recorded.Contains));
    }

    private static string Render(JsonElement e) =>
        $"log: {string.Join(" ", e.GetProperty("log").EnumerateArray().Select(x => x.GetString()))}\n" +
        $"escaped: {(e.GetProperty("escaped").ValueKind == JsonValueKind.Null ? "-" : e.GetProperty("escaped").GetString())}\n" +
        $"later: {string.Join(" ", e.GetProperty("later").EnumerateArray().Select(x => x.GetString()))}\n" +
        $"later escaped: {(e.GetProperty("laterEscaped").ValueKind == JsonValueKind.Null ? "-" : e.GetProperty("laterEscaped").GetString())}\n" +
        $"content: {JsonSerializer.Serialize(e.GetProperty("content"))}\n" +
        $"broken: {e.GetProperty("broken").GetBoolean()}";

    private static string Run(string name)
    {
        var log = new List<string>();
        var throws = new HashSet<string>();
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var a = doc.GetArray("a");
        var m = doc.GetMap("m");
        var t = doc.GetText("t");

        void Maybe(string what)
        {
            log.Add(what);
            if (throws.Contains(what))
            {
                throw new InvalidOperationException(what);
            }
        }

        a.Observe(_ => Maybe("obs:a"));
        a.Observe(_ => Maybe("obs:a2"));
        m.Observe(_ => Maybe("obs:m"));
        t.Observe(_ => Maybe("obs:t"));
        a.ObserveDeep((_, _) => Maybe("deep:a"));
        m.ObserveDeep((_, _) => Maybe("deep:m"));
        doc.BeforeAllTransactions += (_, _) => Maybe("beforeAllTransactions");
        doc.BeforeTransaction += (_, _) => Maybe("beforeTransaction");
        doc.BeforeObserverCalls += (_, _) => Maybe("beforeObserverCalls");
        doc.AfterTransaction += (_, _) => Maybe("afterTransaction");
        doc.AfterTransaction += (_, _) => Maybe("afterTransaction2");
        doc.AfterTransactionCleanup += (_, _) => Maybe("afterTransactionCleanup");
        doc.Updated += (_, _) => Maybe("update");
        doc.UpdatedV2 += (_, _) => Maybe("updateV2");
        doc.SubdocsChanged += (_, _) => Maybe("subdocs");
        doc.AfterAllTransactions += (_, _) => Maybe("afterAllTransactions");

        void Change() => doc.Transact(_ =>
        {
            a.Insert(0, [1]);
            m["k"] = 1;
            t.Insert(0, "x");
        });

        Action act = Change;
        switch (name)
        {
            case "nothing throws":
                break;
            case "an observer":
                throws.Add("obs:a");
                break;
            case "two observers of one type":
                throws.UnionWith(["obs:a", "obs:a2"]);
                break;
            case "observers of two types":
                throws.UnionWith(["obs:a", "obs:m"]);
                break;
            case "beforeObserverCalls":
                throws.Add("beforeObserverCalls");
                break;
            case "a deep observer":
                throws.Add("deep:a");
                break;
            case "a deep observer and an observer":
                throws.UnionWith(["deep:a", "obs:t"]);
                break;
            case "afterTransaction":
                throws.Add("afterTransaction");
                break;
            case "afterTransaction and an observer":
                throws.UnionWith(["afterTransaction", "obs:m"]);
                break;
            case "afterTransactionCleanup":
            case "update":
            case "updateV2":
            case "afterAllTransactions":
            case "beforeAllTransactions":
            case "beforeTransaction":
                throws.Add(name);
                break;
            case "subdocs":
                throws.Add(name);
                act = () => doc.Transact(_ => m["sub"] = new YDoc(new YDocOptions { Guid = "s" }));
                break;
            case "the function and an observer":
                throws.Add("obs:m");
                act = () => doc.Transact((Action<Transaction>)(_ =>
                {
                    a.Insert(0, [1]);
                    m["k"] = 1;
                    throw new InvalidOperationException("function");
                }));
                break;
            case "the function":
                act = () => doc.Transact((Action<Transaction>)(_ =>
                {
                    a.Insert(0, [1]);
                    throw new InvalidOperationException("function");
                }));
                break;
            case "an observer that changes the document, then one of its transaction":
                {
                    throws.Add("obs:m");
                    var once = false;
                    a.Observe(_ =>
                    {
                        if (!once)
                        {
                            once = true;
                            log.Add("obs:a changes m");
                            m["n"] = 2;
                        }
                    });
                    break;
                }

            case "an observer of a nested transaction and of the first":
                {
                    throws.Add("obs:a");
                    var once = false;
                    a.Observe(_ =>
                    {
                        if (!once)
                        {
                            once = true;
                            m["n"] = 2;
                        }
                    });
                    m.Observe(_ =>
                    {
                        if (m.ContainsKey("n"))
                        {
                            log.Add("obs:m nested");
                            throw new InvalidOperationException("obs:m nested");
                        }
                    });
                    break;
                }

            default:
                throw new ArgumentException(name);
        }

        string escaped = null;
        try
        {
            act();
        }
        catch (InvalidOperationException e)
        {
            escaped = e.Message;
        }

        var first = string.Join(" ", log);
        log.Clear();
        throws.Clear();
        string laterEscaped = null;
        try
        {
            doc.Transact(_ => a.Insert(0, ["later"]));
        }
        catch (InvalidOperationException e)
        {
            laterEscaped = e.Message;
        }

        var broken = doc._transaction != null || doc._transactionCleanups.Count > 0;
        return $"log: {first}\nescaped: {escaped ?? "-"}\nlater: {string.Join(" ", log)}\nlater escaped: {laterEscaped ?? "-"}\n" +
            $"content: {YJson.Stringify(a.ToJson())}\nbroken: {broken}";
    }
}
