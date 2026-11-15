// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// Yjs 13.6.32's <c>PermanentUserData</c>: which user each client id belongs to, and what each user
/// deleted, stored in the document (a map of user description to <c>{ ids, ds }</c>), so that
/// <see cref="GetUserByDeletedId"/> can say who deleted an item.
/// <br/>
/// The map is written by other peers too: entries of another shape are ignored, and
/// <see cref="SetUserMapping"/> replaces one for its own user. Yjs defers its two writes (a user's new
/// deletions, and the data of a user whose entry another peer replaced) with <c>setTimeout</c>; here they
/// run once the batch of transactions that caused them has ended (after its <c>Update</c> events), each
/// in a transaction of its own. The filter of <see cref="SetUserMapping"/> gets the transaction only
/// (the delete set is internal).
/// </summary>
public sealed class PermanentUserData
{
    // JavaScript's safe integers: the client ids a Yjs peer can write.
    private const double MaxSafeInteger = (1L << 53) - 1;

    public YDoc Doc { get; }

    public YMap YUsers { get; }

    /// <summary>Client id to user description; Yjs' <c>clients</c>.</summary>
    internal readonly Dictionary<long, string> Clients = new Dictionary<long, string>();

    /// <summary>User description to what that user deleted; Yjs' <c>dss</c>.</summary>
    internal readonly Dictionary<string, DeleteSet> Dss = new Dictionary<string, DeleteSet>();

    // The writes Yjs runs in a later task, in order; Flush runs them after the current batch.
    private readonly List<Action> _deferred = new List<Action>();

    public PermanentUserData(YDoc doc, YMap storeType = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        storeType ??= doc.GetMap("users");
        Doc = doc;
        YUsers = storeType;
        doc.AfterAllTransactions += Flush;

        storeType.EventHandler += (s, e) =>
        {
            foreach (var userDescription in ((YMapEvent)e.Event).KeysChanged)
            {
                if (UserOf(storeType, userDescription) is YMap user)
                {
                    InitUser(user, userDescription);
                }
            }
        };

        foreach (var kvp in storeType)
        {
            if (UserOf(storeType, kvp.Key) is YMap user)
            {
                InitUser(user, kvp.Key);
            }
        }
    }

    private void InitUser(YMap user, string userDescription)
    {
        var ds = (YArray)user.Get("ds");
        var ids = (YArray)user.Get("ids");

        ds.EventHandler += (s, e) =>
        {
            foreach (var item in e.Event.Changes.Added)
            {
                foreach (var content in item.Content.GetContent())
                {
                    if (content is byte[] encodedDs && TryDecodeDeleteSet(encodedDs) is DeleteSet decoded)
                    {
                        var existing = Dss.TryGetValue(userDescription, out var prev) ? prev : new DeleteSet();
                        Dss[userDescription] = new DeleteSet(new List<DeleteSet> { existing, decoded });
                    }
                }
            }
        };

        Dss[userDescription] = new DeleteSet(ds.OfType<byte[]>().Select(TryDecodeDeleteSet).Where(d => d != null).ToList());

        ids.EventHandler += (s, e) =>
        {
            foreach (var item in e.Event.Changes.Added)
            {
                foreach (var content in item.Content.GetContent())
                {
                    AddClientId(content, userDescription);
                }
            }
        };

        foreach (var id in ids)
        {
            AddClientId(id, userDescription);
        }
    }

    private void AddClientId(object clientIdValue, string userDescription)
    {
        // Written by other peers: anything but a client id is ignored.
        double? id = clientIdValue switch
        {
            int i => i,
            long l => l,
            double d => d,
            _ => null,
        };

        if (id is double value && value >= 0 && value <= MaxSafeInteger && Math.Floor(value) == value)
        {
            Clients[(long)value] = userDescription;
        }
    }

    /// <summary>The user map of <paramref name="userDescription"/>, or null when there is none of the expected shape.</summary>
    private static YMap UserOf(YMap users, string userDescription) =>
        users.ContainsKey(userDescription) && users.Get(userDescription) is YMap user && ArrayOf(user, "ids") != null && ArrayOf(user, "ds") != null
            ? user
            : null;

    private static YArray ArrayOf(YMap user, string key) => user.ContainsKey(key) ? user.Get(key) as YArray : null;

    /// <summary>
    /// Yjs parity: <c>setUserMapping(doc, clientid, userDescription, { filter })</c>: records that
    /// <paramref name="clientId"/> belongs to <paramref name="userDescription"/>, and from now on every local
    /// transaction of <paramref name="doc"/> that deletes something (and passes <paramref name="filter"/>)
    /// adds its deletions to that user.
    /// </summary>
    public void SetUserMapping(YDoc doc, long clientId, string userDescription, Func<Transaction, bool> filter = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        filter ??= _ => true;

        var users = YUsers;
        var user = UserOf(users, userDescription);
        if (user == null)
        {
            user = new YMap();
            user.Set("ids", new YArray());
            user.Set("ds", new YArray());
            users.Set(userDescription, user);
        }

        ArrayOf(user, "ids").Add(new object[] { clientId });

        // When another peer replaces this user's entry, the client ids and deletions known so far are
        // copied into the new one.
        users.EventHandler += (s, e) => _deferred.Add(() =>
        {
            var overwrite = UserOf(users, userDescription);
            if (overwrite == null || ReferenceEquals(overwrite, user))
            {
                return;
            }

            user = overwrite;
            foreach (var (id, description) in Clients)
            {
                if (description == userDescription)
                {
                    ArrayOf(user, "ids")?.Add(new object[] { id });
                }
            }

            if (Dss.TryGetValue(userDescription, out var known))
            {
                ArrayOf(user, "ds")?.Add(new object[] { EncodeDeleteSet(known) });
            }
        });

        if (doc != Doc)
        {
            doc.AfterAllTransactions -= Flush;
            doc.AfterAllTransactions += Flush;
        }

        doc.AfterTransaction += (s, tr) =>
        {
            // The transaction is reused once its batch has ended: what the write needs is read now.
            var ds = tr.DeleteSet;
            if (tr.Local && ds.Clients.Count > 0 && filter(tr))
            {
                var encoded = EncodeDeleteSet(ds);
                _deferred.Add(() => ArrayOf(user, "ds")?.Add(new object[] { encoded }));
            }
        };
    }

    private void Flush(object sender, IList<Transaction> transactions)
    {
        if (_deferred.Count == 0)
        {
            return;
        }

        var writes = _deferred.ToArray();
        _deferred.Clear();
        foreach (var write in writes)
        {
            write();
        }
    }

    /// <summary>
    /// Yjs parity: <c>getUserByClientId(clientid)</c>.
    /// </summary>
    public string GetUserByClientId(long clientId) => Clients.TryGetValue(clientId, out var v) ? v : null;

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

    /// <summary>The delete set another peer stored, or null when it is malformed (it is ignored, not fatal).</summary>
    private static DeleteSet TryDecodeDeleteSet(byte[] encoded)
    {
        try
        {
            return DeleteSet.Read(new DSDecoderV1(new Lib0Reader(encoded)));
        }
        catch (YUpdateException)
        {
            return null;
        }
    }

    private static byte[] EncodeDeleteSet(DeleteSet ds)
    {
        var encoder = new DSEncoderV1();
        ds.Write(encoder);
        return encoder.ToArray();
    }
}
