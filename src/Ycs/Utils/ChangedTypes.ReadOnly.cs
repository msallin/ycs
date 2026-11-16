#nullable enable

namespace Ycs;

// What Transaction.Changed hands out: the same entries, read-only. The sets are HashSet, SingleKey or the shared
// read-only ones, all of them an IReadOnlySet.
internal sealed partial class ChangedTypes : IReadOnlyDictionary<YType, IReadOnlySet<string?>>
{
    IReadOnlySet<string?> IReadOnlyDictionary<YType, IReadOnlySet<string?>>.this[YType key] => (IReadOnlySet<string?>)this[key];

    IEnumerable<YType> IReadOnlyDictionary<YType, IReadOnlySet<string?>>.Keys => Pairs().Select(static kvp => kvp.Key);

    IEnumerable<IReadOnlySet<string?>> IReadOnlyDictionary<YType, IReadOnlySet<string?>>.Values =>
        Pairs().Select(static kvp => (IReadOnlySet<string?>)kvp.Value);

    bool IReadOnlyDictionary<YType, IReadOnlySet<string?>>.TryGetValue(YType key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IReadOnlySet<string?> value)
    {
        if (TryGetValue(key, out var set))
        {
            value = (IReadOnlySet<string?>)set;
            return true;
        }

        value = null;
        return false;
    }

    IEnumerator<KeyValuePair<YType, IReadOnlySet<string?>>> IEnumerable<KeyValuePair<YType, IReadOnlySet<string?>>>.GetEnumerator()
    {
        foreach (var (type, set) in this)
        {
            yield return new(type, (IReadOnlySet<string?>)set);
        }
    }
}
