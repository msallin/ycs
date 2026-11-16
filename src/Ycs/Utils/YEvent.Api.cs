// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public partial class YEvent
{
    public AbstractType Target { get; set; }
    public AbstractType CurrentTarget { get; set; }
    public Transaction Transaction { get; set; }

    public IReadOnlyCollection<object> Path => GetPathTo(CurrentTarget, Target);

    /// <summary>
    /// Mirrors Yjs' <c>event.delta</c>: a shortcut for <c>Changes.Delta</c>. <see cref="YTextEvent"/>
    /// overrides this with its own Quill-style delta computation, exactly as Yjs does.
    /// </summary>
    public virtual IList<Delta> Delta => Changes.Delta;

    /// <summary>
    /// Mirrors Yjs' <c>event.keys</c>: the per-key add/update/delete summary for this event's target.
    /// This is the same map exposed via <see cref="Changes"/>.Keys, computed once and cached.
    /// </summary>
    public IDictionary<string, ChangeKey> Keys => CollectKeys();

    /// <summary>
    /// What changed (Yjs' <c>event.changes</c>): the added and deleted items, the list delta and the
    /// keys. A text overrides it with its own form (see <see cref="YTextEvent.Changes"/>).
    /// </summary>
    public virtual ChangesCollection Changes => CollectChanges();

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
