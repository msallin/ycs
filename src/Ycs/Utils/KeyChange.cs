namespace Ycs;

/// <summary>What a transaction did to a map key (Yjs' <c>{ action, oldValue }</c> of <c>event.keys</c>).</summary>
public enum KeyChangeAction
{
    Add,
    Update,
    Delete
}

/// <summary>
/// The change of one map key by one transaction. <paramref name="OldValue"/> is the value the key held before
/// (null for an <see cref="KeyChangeAction.Add"/>, and for a value that is itself null).
/// </summary>
public readonly record struct KeyChange(KeyChangeAction Action, object? OldValue);
