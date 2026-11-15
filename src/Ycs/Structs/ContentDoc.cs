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

    private YDoc? _doc;

    // Read from an update: the document is created when it is first needed, at the latest when the
    // item is integrated (with the parent's limits), not while an update that may be rejected is decoded.
    private readonly Options _read;

    /// <remarks>
    /// Yjs logs an error for a document that is integrated already and goes on; the document then
    /// belongs to the item integrated last.
    /// </remarks>
    internal ContentDoc(YDoc doc)
    {
        _doc = doc;
        Opts = OptsOf(doc.Gc, doc.AutoLoad, doc.Meta);
    }

    private ContentDoc(Options read)
    {
        _read = read;
        Opts = OptsOf(read.Gc, read.AutoLoad, read.Meta);
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    // Replaced by a fresh document with the same guid when the subdocument is destroyed; never null.
    public YDoc Doc
    {
        get => _doc ??= _read.Create(limits: null);
        internal set => _doc = value;
    }

    // Fixed at construction; Copy and the replacement of a destroyed subdocument start from them.
    internal Dictionary<string, object> Opts { get; }

    private string Guid => _doc?.Guid ?? _read.Guid;

    public IReadOnlyList<object> GetContent() => new[] { Doc };

    public IContent Copy() => new ContentDoc(Options.Parse(Guid, Opts, shouldLoad: null));

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
        _doc ??= _read.Create(transaction.Doc.Limits);

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
        encoder.WriteString(Guid);
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

        return new ContentDoc(Options.Parse(guid, opts, shouldLoad: null));
    }

    private static readonly Dictionary<string, object> EmptyOpts = new();

    /// <summary>What Yjs writes, in its order: gc only when false, autoLoad only when true, meta when set.</summary>
    private static Dictionary<string, object> OptsOf(bool gc, bool autoLoad, object? meta)
    {
        var opts = new Dictionary<string, object>();
        if (!gc)
        {
            opts["gc"] = false;
        }

        if (autoLoad)
        {
            opts["autoLoad"] = true;
        }

        if (meta != null)
        {
            opts["meta"] = meta;
        }

        return opts;
    }

    /// <summary>
    /// Yjs' <c>new Doc({ guid, ...opts, shouldLoad })</c>, with <paramref name="limits"/>: see
    /// <see cref="Options.Parse"/>.
    /// </summary>
    internal static YDoc CreateDocFromOpts(string guid, IDictionary<string, object> opts, bool? shouldLoad, UpdateLimits? limits) =>
        Options.Parse(guid, opts, shouldLoad).Create(limits);

    /// <summary>A subdocument's options as Yjs reads them.</summary>
    private readonly record struct Options(string Guid, bool Gc, bool AutoLoad, object? Meta, string? CollectionId, bool ShouldLoad)
    {
        /// <summary>
        /// A key present in <paramref name="opts"/> overrides, a 'guid' included. <paramref name="shouldLoad"/>
        /// null means Yjs' <c>createDocFromOpts</c>: <c>opts.shouldLoad || opts.autoLoad</c>.
        /// </summary>
        public static Options Parse(string guid, IDictionary<string, object> opts, bool? shouldLoad)
        {
            var autoLoad = opts.TryGetValue("autoLoad", out var al) && Truthy(al);
            string? collectionId = null;
            if (opts.TryGetValue("collectionid", out var cid) && cid != null)
            {
                collectionId = cid as string ?? throw new YUpdateException("A subdocument's 'collectionid' is not a string.");
            }

            return new Options(
                opts.TryGetValue("guid", out var g) ? g as string ?? throw new YUpdateException("A subdocument's 'guid' is not a string.") : guid,
                !opts.TryGetValue("gc", out var gc) || Truthy(gc),
                autoLoad,
                opts.TryGetValue("meta", out var meta) ? meta : null,
                collectionId,
                shouldLoad ?? ((opts.TryGetValue("shouldLoad", out var sl) && Truthy(sl)) || autoLoad));
        }

        public YDoc Create(UpdateLimits? limits) => new(new YDocOptions
        {
            Guid = Guid,
            Gc = Gc,
            AutoLoad = AutoLoad,
            Meta = Meta,
            CollectionId = CollectionId,
            ShouldLoad = ShouldLoad,
            Limits = limits ?? UpdateLimits.Default,
        });
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
