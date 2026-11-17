using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The operations docs/api.md lists as recursing once per level of nested shared types, on a thread with a stack of
/// 1 MiB (3,000 levels) or 256 KiB (100,000 levels). Applying and encoding do not recurse, at the default limit
/// of 3,000 levels or without any.
/// </summary>
[TestClass]
public class DeepNestingStackTests
{
    private const int Levels = 3_000;

    private const int HostileLevels = 100_000;

    private static void NestMaps(YMap map, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            var child = new YMap();
            map["c"] = child;
            map = child;
        }
    }

    private static byte[] NestedMapsUpdate(int depth)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, Limits = UpdateLimits.Unlimited });
        doc.Transact(_ => NestMaps(doc.GetMap("m"), depth));
        return doc.EncodeStateAsUpdate();
    }

    private static Exception OnStack(int bytes, Action action)
    {
        Exception error = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
            },
            bytes);
        thread.Start();
        thread.Join();
        return error;
    }

    [TestMethod]
    public void ApplyUpdate_3000NestedMapsUnlimited_IsAppliedAndEncodesBackOnA1MiBStack()
    {
        var update = NestedMapsUpdate(Levels);
        byte[] encoded = null;
        byte[] sv = null;

        var error = OnStack(1024 * 1024, () =>
        {
            var target = new YDoc(new YDocOptions { ClientId = 9, Limits = UpdateLimits.Unlimited });
            target.ApplyUpdate(update, limits: UpdateLimits.Unlimited);
            encoded = target.EncodeStateAsUpdate();
            sv = target.EncodeStateVector();
        });

        Assert.AreEqual((null, Convert.ToBase64String(update), true), (error?.GetType(), Convert.ToBase64String(encoded ?? []), sv is { Length: > 1 }));
    }

    [TestMethod]
    public void ApplyUpdate_OneLevelBeyondTheDefaultMaxDepth_IsRejectedAndTheTargetStaysEmpty()
    {
        var update = NestedMapsUpdate(Levels + 1);
        var target = new YDoc();

        var thrown = Assert.ThrowsExactly<YUpdateException>(() => target.ApplyUpdate(update));

        Assert.AreEqual((true, "AA=="), (thrown.Message.Contains("UpdateLimits.MaxDepth"), Convert.ToBase64String(target.EncodeStateVector())));
    }

    [TestMethod]
    public void Clone_100000NestedMapsOnA256KiBStack_ThrowsInsufficientExecutionStack()
    {
        var update = NestedMapsUpdate(HostileLevels);
        var target = new YDoc(new YDocOptions { ClientId = 9, Limits = UpdateLimits.Unlimited });
        target.ApplyUpdate(update, limits: UpdateLimits.Unlimited);
        var map = target.GetMap("m");

        var error = OnStack(256 * 1024, () => map.Clone());

        Assert.IsInstanceOfType<InsufficientExecutionStackException>(error);
    }

    [TestMethod]
    public void ToString_100000NestedXmlElementsOnA256KiBStack_ThrowsInsufficientExecutionStack()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, Limits = UpdateLimits.Unlimited });
        var root = doc.GetXmlFragment("x");
        doc.Transact(_ =>
        {
            YXmlFragment parent = root;
            for (var i = 0; i < HostileLevels; i++)
            {
                var child = new YXmlElement("p");
                parent.Add([child]);
                parent = child;
            }
        });

        var error = OnStack(256 * 1024, () => root.ToString());

        Assert.IsInstanceOfType<InsufficientExecutionStackException>(error);
    }
}
