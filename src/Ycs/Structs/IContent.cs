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

    void Integrate(Transaction transaction, ItemRef item);
    void Delete(Transaction transaction);
    void Gc(StructStore store);
    void Write(IUpdateEncoder encoder, int offset);
}
