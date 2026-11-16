// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public partial class Transaction
{
    /// <summary>
    /// The Yjs instance.
    /// </summary>
    public YDoc Doc => _ended ? throw EndedException() : _doc;

    public object Origin => _ended ? throw EndedException() : _origin;

    /// <summary>
    /// Holds the state before the transaction started.
    /// </summary>
    public IDictionary<long, long> BeforeState => _ended ? throw EndedException() : _beforeState;

    /// <summary>
    /// Holds the state after the transaction (empty until its cleanup starts).
    /// </summary>
    public IDictionary<long, long> AfterState => _ended ? throw EndedException() : _afterState;

    /// <summary>
    /// All types that were directly modified (property added or child
    /// inserted/deleted). New types are not included in this Set.
    /// Maps from type to parentSubs ('item.parentSub = null' for YArray).
    /// </summary>
    public IDictionary<AbstractType, ISet<string>> Changed => _ended ? throw EndedException() : _changed;

    /// <summary>
    /// Stores the events for the types that observe also child elements.
    /// It is mainly used by 'observeDeep'.
    /// </summary>
    public IDictionary<AbstractType, IList<YEvent>> ChangedParentTypes => _ended ? throw EndedException() : _changedParentTypes;

    /// <summary>
    /// Stores meta information on the transaction.
    /// </summary>
    public IDictionary<string, object> Meta => _ended ? throw EndedException() : _meta ??= new Dictionary<string, object>();

    /// <summary>
    /// Whether this change originates from this doc. Applying an update makes the transaction it
    /// runs in non-local, as in Yjs.
    /// </summary>
    public bool Local
    {
        get => _ended ? throw EndedException() : _local;
        internal set => _local = value;
    }

    public ISet<YDoc> SubdocsAdded => _ended ? throw EndedException() : _subdocsAdded ??= new HashSet<YDoc>();

    public ISet<YDoc> SubdocsRemoved => _ended ? throw EndedException() : _subdocsRemoved ??= new HashSet<YDoc>();

    public ISet<YDoc> SubdocsLoaded => _ended ? throw EndedException() : _subdocsLoaded ??= new HashSet<YDoc>();
}
