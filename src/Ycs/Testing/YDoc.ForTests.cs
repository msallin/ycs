namespace Ycs;

// The per-document test switches (the process-wide ones are in TestHooks) and what the tests read of a document.
public sealed partial class YDoc
{
    /// <summary>No search markers are made or used in this document (the tests' reference: markers must not change any output).</summary>
    internal bool SearchMarkersDisabledForTests;

    /// <summary>Every insert integrates an item of its own, as in Yjs (the tests' reference for <see cref="ItemRef.ExtendRun"/>).</summary>
    internal bool RunExtensionsDisabledForTests;

    /// <summary>The marker lookups walk as Yjs' loops do, item by item (the tests' reference for the run-head index, <see cref="ClientStructs.RunHead"/>).</summary>
    internal bool RunHeadIndexDisabledForTests;

    /// <summary>How many steps a marker walk takes item by item before it asks the run-head index (the tests lower it to 1).</summary>
    internal int LiteralWalkStepsForTests = 32;

    /// <summary>When set, every marker lookup adds the item and index it ends at (the tests compare them).</summary>
    internal List<(ItemId Item, int Index)>? MarkerLookupsForTests;

    /// <summary>The root types by name, as defined so far.</summary>
    internal IEnumerable<KeyValuePair<string, YType>> RootsForTests => _share ?? [];
}
