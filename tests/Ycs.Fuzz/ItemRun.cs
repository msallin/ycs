namespace Ycs.Corpus;

/// <summary>One struct of a sequence's item list, as tests/Ycs.Corpus/YcsAdapter.cs defines it for ItemListInvariants.</summary>
internal readonly record struct ItemRun(
    long Client,
    long Clock,
    int Length,
    (long Client, long Clock)? Origin,
    (long Client, long Clock)? RightOrigin,
    bool Deleted,
    string Kind,
    bool LinkedBack,
    bool InParent)
{
    public const string ItemKind = "Item";
}
