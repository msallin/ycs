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
/// order, which exception escapes, and what a later change of the document still calls.
/// </summary>
[TestClass]
public class ThrowingObserverTests
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "throwing-observers.json"))).RootElement.GetProperty("scenarios"));

    public static IEnumerable<object[]> Scenarios => Fixture.Value.EnumerateObject().Select(p => new object[] { p.Name });

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Scenario_AsYjs(string name)
    {
        var expected = Fixture.Value.GetProperty(name);

        var actual = Run(name);

        Assert.AreEqual(Render(expected), actual);
    }

    private static string Render(JsonElement e) =>
        $"log: {string.Join(" ", e.GetProperty("log").EnumerateArray().Select(x => x.GetString()))}\n" +
        $"escaped: {(e.GetProperty("escaped").ValueKind == JsonValueKind.Null ? "-" : e.GetProperty("escaped").GetString())}\n" +
        $"later: {string.Join(" ", e.GetProperty("later").EnumerateArray().Select(x => x.GetString()))}\n" +
        $"later escaped: {(e.GetProperty("laterEscaped").ValueKind == JsonValueKind.Null ? "-" : e.GetProperty("laterEscaped").GetString())}\n" +
        $"content: {JsonSerializer.Serialize(e.GetProperty("content"))}";

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

        a.EventHandler += (_, _) => Maybe("obs:a");
        a.EventHandler += (_, _) => Maybe("obs:a2");
        m.EventHandler += (_, _) => Maybe("obs:m");
        t.EventHandler += (_, _) => Maybe("obs:t");
        a.DeepEventHandler += (_, _) => Maybe("deep:a");
        m.DeepEventHandler += (_, _) => Maybe("deep:m");
        doc.BeforeObserverCalls += (_, _) => Maybe("beforeObserverCalls");
        doc.AfterTransaction += (_, _) => Maybe("afterTransaction");
        doc.AfterTransaction += (_, _) => Maybe("afterTransaction2");
        doc.AfterTransactionCleanup += (_, _) => Maybe("afterTransactionCleanup");
        doc.Update += (_, _) => Maybe("update");
        doc.UpdateV2 += (_, _) => Maybe("updateV2");
        doc.AfterAllTransactions += (_, _) => Maybe("afterAllTransactions");

        void Change() => doc.Transact(_ =>
        {
            a.Insert(0, [1]);
            m.Set("k", 1);
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
                throws.Add(name);
                break;
            case "the function and an observer":
                throws.Add("obs:m");
                act = () => doc.Transact(_ =>
                {
                    a.Insert(0, [1]);
                    m.Set("k", 1);
                    throw new InvalidOperationException("function");
                });
                break;
            case "the function":
                act = () => doc.Transact(_ =>
                {
                    a.Insert(0, [1]);
                    throw new InvalidOperationException("function");
                });
                break;
            case "an observer that changes the document, then one of its transaction":
                {
                    throws.Add("obs:m");
                    var once = false;
                    a.EventHandler += (_, _) =>
                    {
                        if (!once)
                        {
                            once = true;
                            log.Add("obs:a changes m");
                            m.Set("n", 2);
                        }
                    };
                    break;
                }

            case "an observer of a nested transaction and of the first":
                {
                    throws.Add("obs:a");
                    var once = false;
                    a.EventHandler += (_, _) =>
                    {
                        if (!once)
                        {
                            once = true;
                            m.Set("n", 2);
                        }
                    };
                    m.EventHandler += (_, _) =>
                    {
                        if (m.ContainsKey("n"))
                        {
                            log.Add("obs:m nested");
                            throw new InvalidOperationException("obs:m nested");
                        }
                    };
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

        return $"log: {first}\nescaped: {escaped ?? "-"}\nlater: {string.Join(" ", log)}\nlater escaped: {laterEscaped ?? "-"}\n" +
            $"content: {YJson.Stringify(a.ToJson())}";
    }
}
