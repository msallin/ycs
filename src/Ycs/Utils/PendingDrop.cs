namespace Ycs;

/// <summary>
/// What <see cref="YDoc.PendingDropped"/> reports: the waiting structs that were dropped. <paramref name="Structs"/>
/// counts the structs as they were written (Skip structs included); <paramref name="Clients"/> lists the clients
/// they were of.
/// </summary>
public readonly record struct PendingDrop(long Structs, IReadOnlyList<long> Clients);
