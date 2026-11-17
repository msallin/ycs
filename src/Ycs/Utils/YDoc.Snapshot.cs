#nullable enable

namespace Ycs;

public partial class YDoc
{
    /// <summary>
    /// Yjs parity: <c>createDocFromSnapshot(originDoc, snapshot, newDoc)</c> (src/utils/Snapshot.js).
    /// A snapshot that holds a client or clock this document does not have throws <see cref="YUpdateException"/>.
    /// </summary>
    /// <example>
    ///   var doc = new YDoc(new YDocOptions { GarbageCollection = false });
    ///   doc.GetText().Insert(0, "world!");
    ///   var snapshot = doc.CreateSnapshot();
    ///   doc.GetText().Insert(0, "hello ");
    ///   var restored = doc.CreateDocFromSnapshot(snapshot);
    ///   // restored.GetText().ToString() == "world!"
    /// </example>
    public YDoc CreateDocFromSnapshot(Snapshot snapshot, YDoc? newDoc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (GarbageCollection)
        {
            // Restoring a GC-ed document is not possible: some of the restored items would have
            // had their content collected already.
            throw new InvalidOperationException("Garbage collection must be disabled in the origin document.");
        }

        var update = Snapshot.EncodeRestoreUpdateV2(this, snapshot.DeleteSet, snapshot.StateVector);
        var target = newDoc ?? new YDoc();
        target.ApplyUpdate(update, "snapshot", UpdateFormat.V2);
        return target;
    }
}
