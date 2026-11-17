namespace Ycs;

internal interface IContent
{
    bool Countable { get; }
    int Length { get; }
    IReadOnlyList<object> GetContent();
    IContent Copy();
    IContent Splice(int offset);
    bool MergeWith(IContent right);
}

internal interface IContentEx : IContent
{
    int Ref { get; }

    /// <summary>Whether <see cref="IContent.MergeWith"/> merges content of the same kind (it then always does).</summary>
    bool Mergeable { get; }

    void Integrate(Transaction transaction, ItemRef item);
    void Delete(Transaction transaction);
    void Gc(StructStore store);
    void Write(IUpdateEncoder encoder, int offset);
}
