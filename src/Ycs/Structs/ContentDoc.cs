// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

internal class ContentDoc : IContentEx
{
    internal const int _ref = 9;

    internal ContentDoc(YDoc doc)
    {
        if (doc._item != null)
        {
            throw new Exception("This document was already integrated as a sub-document. You should create a second instance instead with the same guid.");
        }

        Doc = doc;
        _guid = doc.Guid;
        Opts = new YDocOptions();

        if (!doc.Gc)
        {
            Opts.Gc = false;
        }

        if (doc.AutoLoad)
        {
            Opts.AutoLoad = true;
        }

        if (doc.Meta != null)
        {
            Opts.Meta = doc.Meta;
        }
    }

    // Doc is null once a deleted subdocument was destroyed; the item still encodes its guid.
    private readonly string _guid;

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public YDoc Doc { get; internal set; }
    public YDocOptions Opts { get; internal set; } = new YDocOptions();

    public IReadOnlyList<object> GetContent() => new[] { Doc };

    // As Yjs: a new document with the same guid and options (the original is integrated already).
    public IContent Copy()
    {
        var opts = Opts.Clone();
        opts.Guid = _guid;
        return new ContentDoc(new YDoc(opts));
    }

    public IContent Splice(int offset)
    {
        throw new NotImplementedException();
    }

    public bool MergeWith(IContent right)
    {
        return false;
    }

    void IContentEx.Integrate(Transaction transaction, Item item)
    {
        // This needs to be reflected in doc.destroy as well.
        Doc._item = item;
        transaction.SubdocsAdded.Add(Doc);

        if (Doc.ShouldLoad)
        {
            transaction.SubdocsLoaded.Add(Doc);
        }
    }

    void IContentEx.Delete(Transaction transaction)
    {
        if (transaction.SubdocsAdded.Contains(Doc))
        {
            transaction.SubdocsAdded.Remove(Doc);
        }
        else
        {
            transaction.SubdocsRemoved.Add(Doc);
        }
    }

    void IContentEx.Gc(StructStore store)
    {
        // Do nothing.
    }

    void IContentEx.Write(IUpdateEncoder encoder, int offset)
    {
        // 32 digits separated by hyphens, no braces.
        encoder.WriteString(_guid);
        Opts.Write(encoder, offset);
    }

    internal static ContentDoc Read(IUpdateDecoder decoder)
    {
        var guidStr = decoder.ReadString();

        var opts = YDocOptions.Read(decoder);
        opts.Guid = guidStr;

        return new ContentDoc(new YDoc(opts));
    }
}
