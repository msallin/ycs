namespace Ycs;

public partial class YDoc
{
    /// <summary>
    /// Whether a document-level handler sees transactions after their observers ran, and with them
    /// <see cref="Transaction.ChangedParentTypes"/> and the events in it (an <see cref="UndoManager"/>
    /// is such a handler). Without one, a changed type builds its event only when it or an ancestor
    /// is observed (<see cref="YType.IsObserved"/>).
    /// </summary>
    internal bool HasTransactionListeners => AfterTransaction != null || AfterTransactionCleanup != null || AfterAllTransactions != null;
}
