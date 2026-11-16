// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// What a transaction changed in one shared type. The sealed subclasses are <see cref="YArrayEvent"/>,
/// <see cref="YMapEvent"/>, <see cref="YTextEvent"/> and <see cref="YXmlEvent"/>. An event is readable while its
/// transaction runs: after the transaction ended, reading what it did not compute yet throws.
/// </summary>
public abstract partial class YEvent
{
    /// <summary>The type that changed.</summary>
    public YType Target { get; }

    /// <summary>The type whose observer is running: <see cref="Target"/> or, for a deep observer, one of its ancestors.</summary>
    public YType CurrentTarget { get; internal set; }

    public Transaction Transaction { get; internal set; }

    /// <summary>The steps from <see cref="CurrentTarget"/> down to <see cref="Target"/>.</summary>
    public IReadOnlyList<PathSegment> Path => GetPathTo(CurrentTarget, Target);

    /// <summary>
    /// Mirrors Yjs' <c>event.delta</c>: a shortcut for <c>Changes.Delta</c>. <see cref="YTextEvent"/>
    /// overrides this with its own Quill-style delta computation, exactly as Yjs does.
    /// </summary>
    public virtual IReadOnlyList<Delta> Delta => Changes.Delta;

    /// <summary>
    /// Mirrors Yjs' <c>event.keys</c>: the per-key add/update/delete summary for this event's target.
    /// This is the same map exposed via <see cref="Changes"/>.Keys, computed once and cached.
    /// </summary>
    public IReadOnlyDictionary<string, KeyChange> Keys => CollectKeys();

    /// <summary>
    /// What changed (Yjs' <c>event.changes</c>): the added and deleted items, the list delta and the
    /// keys. A text overrides it with its own form (see <see cref="YTextEvent.Changes"/>).
    /// </summary>
    public virtual YChanges Changes => CollectChanges();

    /// <summary>
    /// Check if a struct is deleted by this event.
    /// <br/>
    /// In contrast to <c>change.deleted</c>, this method also returns true if the struct was
    /// added and then deleted.
    /// </summary>
    public bool Deletes(ItemView view) => Transaction.DeleteSet.IsDeleted(view.Id);

    /// <summary>
    /// Check if a struct is added by this event.
    /// <br/>
    /// In contrast to <c>change.added</c>, this method also returns true if the struct was
    /// added and then deleted.
    /// </summary>
    public bool Adds(ItemView view) => !Transaction.BeforeState.TryGetValue(view.Id.Client, out var clock) || view.Id.Clock >= clock;
}
