// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public partial class YDoc
{
    /// <summary>
    /// Yjs parity: <c>createDocFromSnapshot(originDoc, snapshot, newDoc)</c> (src/utils/Snapshot.js).
    /// A snapshot that holds a client or clock this document does not have throws <see cref="YUpdateException"/>.
    /// </summary>
    /// <example>
    ///   var doc = new YDoc(new YDocOptions { Gc = false });
    ///   doc.GetText().Insert(0, "world!");
    ///   var snapshot = doc.CreateSnapshot();
    ///   doc.GetText().Insert(0, "hello ");
    ///   var restored = doc.CreateDocFromSnapshot(snapshot);
    ///   // restored.GetText().ToString() == "world!"
    /// </example>
    public YDoc CreateDocFromSnapshot(Snapshot snapshot, YDoc newDoc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Gc)
        {
            // Restoring a GC-ed document is not possible: some of the restored items would have
            // had their content collected already.
            throw new InvalidOperationException("Garbage collection must be disabled in the origin document.");
        }

        var update = Snapshot.EncodeRestoreUpdateV2(this, snapshot.DeleteSet, snapshot.StateVector);
        var target = newDoc ?? new YDoc();
        target.ApplyUpdateV2(update, transactionOrigin: "snapshot");
        return target;
    }
}
