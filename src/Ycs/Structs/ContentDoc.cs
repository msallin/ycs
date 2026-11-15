// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Numerics;

namespace Ycs;

internal class ContentDoc : IContentEx
{
    internal const int _ref = 9;

    /// <remarks>
    /// Yjs logs an error for a document that is integrated already and goes on; the document then
    /// belongs to the item integrated last.
    /// </remarks>
    internal ContentDoc(YDoc doc)
    {
        Doc = doc;

        // What Yjs writes, in its order: gc only when false, autoLoad only when true, meta when set.
        Opts = new Dictionary<string, object>();
        if (!doc.Gc)
        {
            Opts["gc"] = false;
        }

        if (doc.AutoLoad)
        {
            Opts["autoLoad"] = true;
        }

        if (doc.Meta != null)
        {
            Opts["meta"] = doc.Meta;
        }
    }

    private static readonly Dictionary<string, object> EmptyOpts = new();

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    // Replaced by a fresh document with the same guid when the subdocument is destroyed; never null.
    public YDoc Doc { get; internal set; }

    // Fixed at construction; Copy and the replacement of a destroyed subdocument start from them.
    internal Dictionary<string, object> Opts { get; }

    public IReadOnlyList<object> GetContent() => new[] { Doc };

    public IContent Copy() => new ContentDoc(CreateDocFromOpts(Doc.Guid, Opts, shouldLoad: null));

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
        encoder.WriteString(Doc.Guid);
        encoder.WriteAny(Opts);
    }

    internal static ContentDoc Read(IUpdateDecoder decoder)
    {
        var guid = decoder.ReadString();

        // Yjs spreads the options into an object: any value but null (a TypeError) is accepted, and
        // only an object's keys can name an option.
        var opts = decoder.ReadAny() switch
        {
            null => throw new YUpdateException("A subdocument's options are null."),
            IDictionary<string, object> dict => dict,
            _ => EmptyOpts,
        };

        return new ContentDoc(CreateDocFromOpts(guid, opts, shouldLoad: null));
    }

    /// <summary>
    /// Yjs' <c>new Doc({ guid, ...opts, shouldLoad })</c>: a key present in <paramref name="opts"/>
    /// overrides, a 'guid' included. <paramref name="shouldLoad"/> null means Yjs'
    /// <c>createDocFromOpts</c>: <c>opts.shouldLoad || opts.autoLoad</c>.
    /// </summary>
    internal static YDoc CreateDocFromOpts(string guid, IDictionary<string, object> opts, bool? shouldLoad)
    {
        var options = new YDocOptions
        {
            Guid = opts.TryGetValue("guid", out var g)
                ? g as string ?? throw new YUpdateException("A subdocument's 'guid' is not a string.")
                : guid,
            Gc = !opts.TryGetValue("gc", out var gc) || Truthy(gc),
            AutoLoad = opts.TryGetValue("autoLoad", out var autoLoad) && Truthy(autoLoad),
            Meta = opts.TryGetValue("meta", out var meta) ? meta : null,
        };

        if (opts.TryGetValue("collectionid", out var collectionId) && collectionId != null)
        {
            options.CollectionId = collectionId as string ?? throw new YUpdateException("A subdocument's 'collectionid' is not a string.");
        }

        options.ShouldLoad = shouldLoad
            ?? ((opts.TryGetValue("shouldLoad", out var sl) && Truthy(sl)) || options.AutoLoad);
        return new YDoc(options);
    }

    // JavaScript's truthiness: Yjs only tests these options.
    private static bool Truthy(object value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length != 0,
        double d => d != 0 && !double.IsNaN(d),
        float f => f != 0 && !float.IsNaN(f),
        int i => i != 0,
        long l => l != 0,
        BigInteger bi => !bi.IsZero,
        _ => true,
    };
}
