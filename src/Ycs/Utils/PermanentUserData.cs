// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Ports Yjs 13.6.32's <c>PermanentUserData</c> (src/utils/PermanentUserData.js): a durable
/// clientId-to-user-description mapping, stored as shared state inside the document itself (so it
/// survives reconnects/reloads the way ephemeral awareness state does not), plus a per-user record
/// of everything that clientId ever deleted. This is what "who wrote this" attribution (e.g.
/// y-prosemirror's change-tracking) is built on: given the <see cref="ID"/> of a deleted item,
/// <see cref="GetUserByDeletedId"/> answers which user deleted it.
/// <br/>
/// Deviations from Yjs, both forced by this port's existing (and, per the file-ownership rules for
/// this task, un-editable) internal/public split:
/// <list type="bullet">
/// <item>
/// Yjs stores everything in a <c>Map&lt;number, string&gt;</c> (<c>clients</c>) and
/// <c>Map&lt;string, DeleteSet&gt;</c> (<c>dss</c>) that are plain public fields on the instance.
/// Here <see cref="Ycs.DeleteSet"/> is `internal` (matching <see cref="Snapshot"/>'s own `internal`
/// delete-set field), so the per-user delete-set map is kept as an internal field instead
/// (test code in this assembly can still see it via `InternalsVisibleTo`); only the two accessor
/// methods Yjs exposes (<see cref="GetUserByClientId"/>, <see cref="GetUserByDeletedId"/>) are
/// public, exactly as in Yjs.
/// </item>
/// <item>
/// Yjs's <c>setUserMapping</c> takes a <c>filter(transaction, deleteSet)</c> callback. A public
/// method cannot have a `DeleteSet`-shaped parameter for the same reason above (and `Transaction`'s
/// own `DeleteSet` property is internal too, so an outside caller could not use that argument even
/// if it were exposed), so the filter here is `Func&lt;Transaction, bool&gt;` - everything about
/// the transaction except its raw delete set, which is what real-world filters (e.g. "only record
/// deletions performed by a human, not by the undo manager") key off anyway.
/// </item>
/// <item>
/// Yjs defers the `ds`/`ids` bookkeeping in <c>setUserMapping</c> with `setTimeout(fn, 0)`, and
/// defers `users.observe`'s "user overwritten" handling the same way - both are JS-runtime-loop
/// conveniences (coalescing bursts of changes onto the next microtask; sidestepping mutating a
/// YMap from inside its own synchronous observer callback) rather than something the algorithm
/// depends on for correctness. This port has no directly equivalent runtime hook and no async test
/// harness to match one against, so both run synchronously - `YDoc.Transact` already queues and
/// drains nested transactions started from inside an `AfterTransaction` handler (by the time that
/// event fires, `doc._transaction` has already been cleared - see `Transaction.CleanupTransactions`
/// in Utils/Transaction.cs), so a synchronous `user.Get("ds")`-array push from that handler behaves
/// the same as Yjs' deferred one, just without the extra tick.
/// </item>
/// </list>
/// </summary>
public sealed class PermanentUserData
{
    public readonly YDoc Doc;
    public readonly YMap YUsers;

    /// <summary>Maps clientId -&gt; user description. Yjs parity: <c>this.clients</c>.</summary>
    internal readonly IDictionary<int, string> Clients;

    /// <summary>Maps user description -&gt; everything that user ever deleted. Yjs parity: <c>this.dss</c>.</summary>
    internal readonly IDictionary<string, DeleteSet> Dss;

    public PermanentUserData(YDoc doc, YMap storeType = null)
    {
        storeType = storeType ?? doc.GetMap("users");

        Doc = doc;
        YUsers = storeType;
        Clients = new Dictionary<int, string>();
        Dss = new Dictionary<string, DeleteSet>();

        void InitUser(YMap user, string userDescription)
        {
            var ds = (YArray)user.Get("ds");
            var ids = (YArray)user.Get("ids");

            void AddClientId(object clientIdValue)
            {
                Clients[Convert.ToInt32(clientIdValue)] = userDescription;
            }

            ds.EventHandler += (s, e) =>
            {
                foreach (var item in e.Event.Changes.Added)
                {
                    foreach (var content in item.Content.GetContent())
                    {
                        if (content is byte[] encodedDs)
                        {
                            var existing = Dss.TryGetValue(userDescription, out var prev) ? prev : new DeleteSet();
                            Dss[userDescription] = new DeleteSet(new List<DeleteSet> { existing, DecodeDeleteSet(encodedDs) });
                        }
                    }
                }
            };

            Dss[userDescription] = new DeleteSet(ds.Select(encodedDs => DecodeDeleteSet((byte[])encodedDs)).ToList());

            ids.EventHandler += (s, e) =>
            {
                foreach (var item in e.Event.Changes.Added)
                {
                    foreach (var content in item.Content.GetContent())
                    {
                        AddClientId(content);
                    }
                }
            };

            foreach (var id in ids)
            {
                AddClientId(id);
            }
        }

        // Observe users.
        storeType.EventHandler += (s, e) =>
        {
            var mapEvent = (YMapEvent)e.Event;
            foreach (var userDescription in mapEvent.KeysChanged)
            {
                if (storeType.ContainsKey(userDescription))
                {
                    InitUser((YMap)storeType.Get(userDescription), userDescription);
                }
            }
        };

        // Add initial data.
        foreach (var kvp in storeType)
        {
            InitUser((YMap)kvp.Value, kvp.Key);
        }
    }

    /// <summary>
    /// Yjs parity: <c>setUserMapping(doc, clientid, userDescription, { filter })</c>. See the
    /// class-level remarks for why <paramref name="filter"/> only receives the
    /// <see cref="Transaction"/>, not its delete set.
    /// </summary>
    public void SetUserMapping(YDoc doc, int clientId, string userDescription, Func<Transaction, bool> filter = null)
    {
        filter = filter ?? (_ => true);

        var users = YUsers;
        var user = users.ContainsKey(userDescription) ? (YMap)users.Get(userDescription) : null;

        if (user == null)
        {
            user = new YMap();
            user.Set("ids", new YArray());
            user.Set("ds", new YArray());
            users.Set(userDescription, user);
        }

        ((YArray)user.Get("ids")).Add(new object[] { clientId });

        // If another writer replaces this user's entry (e.g. a concurrent 'setUserMapping' for the
        // same description under a different id), fold what we already know about this client and
        // its deletions into the new object so nothing is lost. Yjs parity: the 'users.observe'
        // handler inside 'setUserMapping'.
        var currentUser = user;
        users.EventHandler += (s, e) =>
        {
            var overwrite = users.ContainsKey(userDescription) ? (YMap)users.Get(userDescription) : null;
            if (overwrite == null || ReferenceEquals(overwrite, currentUser))
            {
                return;
            }

            currentUser = overwrite;

            foreach (var kvp in Clients)
            {
                if (kvp.Value == userDescription)
                {
                    ((YArray)currentUser.Get("ids")).Add(new object[] { kvp.Key });
                }
            }

            if (Dss.TryGetValue(userDescription, out var ds))
            {
                ((YArray)currentUser.Get("ds")).Add(new object[] { EncodeDeleteSet(ds) });
            }
        };

        doc.AfterTransaction += (s, tr) =>
        {
            var ds = tr.DeleteSet;
            if (tr.Local && ds.Clients.Count > 0 && filter(tr))
            {
                ((YArray)currentUser.Get("ds")).Add(new object[] { EncodeDeleteSet(ds) });
            }
        };
    }

    /// <summary>
    /// Yjs parity: <c>getUserByClientId(clientid)</c>.
    /// </summary>
    public string GetUserByClientId(int clientId) => Clients.TryGetValue(clientId, out var v) ? v : null;

    /// <summary>
    /// Yjs parity: <c>getUserByDeletedId(id)</c>.
    /// </summary>
    public string GetUserByDeletedId(ID id)
    {
        foreach (var kvp in Dss)
        {
            if (kvp.Value.IsDeleted(id))
            {
                return kvp.Key;
            }
        }

        return null;
    }

    private static DeleteSet DecodeDeleteSet(byte[] encoded)
    {
        var stream = new Lib0Reader(encoded);
        var decoder = new DSDecoderV1(stream);
        return DeleteSet.Read(decoder);
    }

    private static byte[] EncodeDeleteSet(DeleteSet ds)
    {
        var encoder = new DSEncoderV1();
        ds.Write(encoder);
        return encoder.ToArray();
    }
}
