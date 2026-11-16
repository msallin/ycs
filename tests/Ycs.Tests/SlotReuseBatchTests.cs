using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A slot freed by a merge may be read until the outermost batch of transactions ends (H4: a nested transaction of an
/// observer runs its own merge pass while the outer cleanup still has structs to look at), so it is reused only then.
/// </summary>
[TestClass]
public class SlotReuseBatchTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    [TestMethod]
    public void Allocate_InANestedTransactionOfTheBatchThatFreedASlot_DoesNotTakeIt()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a");
        var freed = -1;
        var takenInNested = false;
        doc.AfterTransactionCleanup += (_, _) =>
        {
            if (freed < 0)
            {
                return;
            }

            var nested = freed;
            freed = -1;
            text.Insert(0, "x");
            text.Insert(0, "y");
            takenInNested = doc.Store.Slab.ReadOwner(nested) != null;
        };

        doc.Transact(_ =>
        {
            text.Insert(1, "b");
            freed = doc.Store.Find(new ItemId(1, 1)).Handle;
        });

        Assert.IsFalse(takenInNested);
    }

    [TestMethod]
    public void Allocate_AfterTheBatchEnded_TakesTheFreedSlot()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a");
        var freed = -1;
        doc.Transact(_ =>
        {
            text.Insert(1, "b");
            freed = doc.Store.Find(new ItemId(1, 1)).Handle;
        });

        text.Insert(0, "x");

        Assert.IsNotNull(doc.Store.Slab.ReadOwner(freed));
    }
}
