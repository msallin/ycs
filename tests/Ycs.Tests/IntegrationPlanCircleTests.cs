using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>An update whose structs wait for each other in a circle leaves in the rest what Yjs' integrateStructs leaves, byte for byte.</summary>
[TestClass]
public class IntegrationPlanCircleTests
{
    // Client 3 holds 3:0 (origin 1:0), 3:1 (origin 3:0) and 3:2 (origin 3:1); client 1 holds 1:0 (origin 3:1). The walk
    // reads 3:0, 1:0, then 3:1, which waits for 3:0: the stack holds 3:0, 1:0 and 3:1.
    // Yjs 13.6 (applyUpdate, then encodeStateAsUpdate) writes 1:0 and 3:1 alone; 3:0 and 3:2 are not kept.
    private const string Circle = "AgMDAIQBAAFhhAMAAWKEAwEBYwEBAIQDAQF4AA==";
    private const string Yjs = "AgEDAYQDAAFiAQEAhAMBAXgA";

    [TestMethod]
    public void ApplyUpdate_StackWithTwoStructsOfOneClient_LeavesTheLatterAloneInTheRest()
    {
        var doc = new YDoc();

        doc.ApplyUpdate(Convert.FromBase64String(Circle));

        Assert.AreEqual((true, Yjs), (doc.HasPending, Convert.ToBase64String(doc.EncodeStateAsUpdate())));
    }
}
