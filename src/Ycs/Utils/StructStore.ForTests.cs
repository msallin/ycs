#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>Merges at the end of a transaction in Yjs' order, pair by pair (the tests' reference; see <see cref="DeleteSet.TryToMergeWithLefts(ClientStructs, int)"/>).</summary>
    internal bool PairwiseMergeForTests;

    /// <summary>The struct lists by client, as the property tests read them (they reach no internal type).</summary>
    internal IEnumerable<KeyValuePair<long, IReadOnlyList<AbstractStruct>>> StructListsForTests() =>
        Clients.Select(kv => new KeyValuePair<long, IReadOnlyList<AbstractStruct>>(kv.Key, kv.Value.Select(s => s.ToStruct()).ToArray()));

    /// <summary>Recounts the struct lists after every transaction and throws on a difference (the tests switch it on).</summary>
    internal static bool CheckAfterTransactionsForTests;

    private long _checks;

    /// <summary>
    /// What <see cref="CheckAfterTransactionsForTests"/> checks: every list (<see cref="ClientStructs.Violations"/>) and
    /// the links and holders around its structs (<c>ConsistencyViolation</c>); a list of more than 1,024 structs only
    /// every count/256th time, so that a test stays linear in its edits.
    /// </summary>
    internal void CheckForTests()
    {
        _checks++;
        HashSet<AbstractType> visited = null;
        foreach (var (client, structs) in Clients)
        {
            if (structs.Count > 1024 && _checks % (structs.Count / 256) != 0)
            {
                continue;
            }

            if (structs.Violations() is { } violation)
            {
                throw new InvalidOperationException($"The structs of client {client} differ from a recount: {violation}.");
            }

            // Two lookups a struct: a list of more than 1,024 is walked every count/32nd time.
            if ((structs.Count <= 1024 || _checks % (structs.Count / 32) == 0) && ConsistencyViolation(structs, ref visited) is { } inconsistency)
            {
                throw new InvalidOperationException($"The structs of client {client} are inconsistent: {inconsistency}.");
            }
        }

        if (CollectedTypeViolation() is { } collected)
        {
            throw new InvalidOperationException($"The store holds a collected type that is not forgotten: {collected}.");
        }
    }
}
