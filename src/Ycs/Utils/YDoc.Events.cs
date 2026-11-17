namespace Ycs;

/// <summary>The handlers of a document, one field for each event of <see cref="YDoc"/>; the document makes this on the first handler added.</summary>
internal sealed class YDocEvents
{
    public EventHandler<Transaction>? BeforeObserverCalls;
    public EventHandler<Transaction>? BeforeTransaction;
    public EventHandler<Transaction>? AfterTransaction;
    public EventHandler<Transaction>? AfterTransactionCleanup;
    public EventHandler? BeforeAllTransactions;
    public EventHandler<IReadOnlyList<Transaction>>? AfterAllTransactions;
    public EventHandler<UpdateEventArgs>? Updated;
    public EventHandler<UpdateEventArgs>? UpdatedV2;
    public EventHandler? Destroyed;
    public EventHandler<PendingDrop>? PendingDropped;
    public EventHandler<SubdocsEventArgs>? SubdocsChanged;

    // As a field-like event: safe against two threads adding or removing at once.
    public static void Add<T>(ref T? field, T? handler)
        where T : Delegate
    {
        var current = field;
        while (true)
        {
            var seen = Interlocked.CompareExchange(ref field, (T?)Delegate.Combine(current, handler), current);
            if (ReferenceEquals(seen, current))
            {
                return;
            }

            current = seen;
        }
    }

    public static void Remove<T>(ref T? field, T? handler)
        where T : Delegate
    {
        var current = field;
        while (true)
        {
            var seen = Interlocked.CompareExchange(ref field, (T?)Delegate.Remove(current, handler), current);
            if (ReferenceEquals(seen, current))
            {
                return;
            }

            current = seen;
        }
    }
}
