#nullable enable

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
    public YDoc Doc { get; }

    /// <summary>The map in the document that holds the users: each description maps to its client ids and deletions.</summary>
    internal YMap Users { get; }

    /// <summary>Client id to user description; Yjs' <c>clients</c>.</summary>
    internal readonly Dictionary<long, string> Clients = new Dictionary<long, string>();

    /// <summary>User description to what that user deleted; Yjs' <c>dss</c>.</summary>
    internal readonly Dictionary<string, DeleteSet> Dss = new Dictionary<string, DeleteSet>();

    // The writes Yjs runs in a later task, in order; Flush runs them after the current batch.
    private readonly List<Action> _deferred = new List<Action>();

    public PermanentUserData(YDoc doc, YMap? store = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var storeType = store ?? doc.GetMap("users");
        Doc = doc;
        Users = storeType;
        doc.AfterAllTransactions += Flush;

        storeType.Observe(e =>
        {
            foreach (var userDescription in e.KeysChanged)
            {
                if (UserOf(storeType, userDescription) is YMap user)
                {
                    InitUser(user, userDescription);
                }
            }
        });

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
        var ds = ArrayOf(user, "ds")!;
        var ids = ArrayOf(user, "ids")!;

        ds.Observe(e =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var content in item.Content)
                {
                    if (content is byte[] encodedDs && TryDecodeDeleteSet(encodedDs) is DeleteSet decoded)
                    {
                        var existing = Dss.TryGetValue(userDescription, out var prev) ? prev : new DeleteSet();
                        Dss[userDescription] = new DeleteSet(new List<DeleteSet> { existing, decoded });
                    }
                }
            }
        });

        Dss[userDescription] = new DeleteSet(ds.Select(v => v.TryGetBytes(out var bytes) ? TryDecodeDeleteSet(bytes.ToArray()) : null).OfType<DeleteSet>().ToList());

        ids.Observe(e =>
        {
            foreach (var item in e.Changes.Added)
            {
                foreach (var content in item.Content)
                {
                    AddClientId(content, userDescription);
                }
            }
        });

        foreach (var id in ids)
        {
            AddClientId(id.Stored(), userDescription);
        }
    }

    private void AddClientId(object? clientIdValue, string userDescription)
    {
        // Written by other peers: anything but a client id is ignored.
        if (JsValue.IsInteger(clientIdValue, out var id) && id >= 0 && id <= JsValue.MaxSafeInteger)
        {
            Clients[id] = userDescription;
        }
    }

    /// <summary>The user map of <paramref name="userDescription"/>, or null when there is none of the expected shape.</summary>
    private static YMap? UserOf(YMap users, string userDescription) =>
        users.TryGetValue(userDescription, out var value) && value.TryGetSharedType(out YMap? user) && ArrayOf(user, "ids") != null && ArrayOf(user, "ds") != null
            ? user
            : null;

    private static YArray? ArrayOf(YMap user, string key) => user.TryGetValue(key, out var value) && value.TryGetSharedType(out YArray? array) ? array : null;

    /// <summary>
    /// Yjs parity: <c>setUserMapping(doc, clientid, userDescription, { filter })</c>: records that
    /// <paramref name="clientId"/> belongs to <paramref name="user"/>, and from now on every local
    /// transaction of <see cref="Doc"/> that deletes something (and passes <paramref name="filter"/>)
    /// adds its deletions to that user.
    /// </summary>
    public void SetUserMapping(long clientId, string user, Func<Transaction, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var userDescription = user;
        filter ??= static _ => true;

        var users = Users;
        var userMap = UserOf(users, userDescription);
        if (userMap == null)
        {
            userMap = new YMap();
            userMap["ids"] = new YArray();
            userMap["ds"] = new YArray();
            users[userDescription] = userMap;
        }

        ArrayOf(userMap, "ids")!.Add(clientId);

        // When another peer replaces this user's entry, the client ids and deletions known so far are
        // copied into the new one.
        users.Observe(e => _deferred.Add(() =>
        {
            var overwrite = UserOf(users, userDescription);
            if (overwrite == null || ReferenceEquals(overwrite, userMap))
            {
                return;
            }

            userMap = overwrite;
            foreach (var (id, description) in Clients)
            {
                if (description == userDescription)
                {
                    ArrayOf(userMap, "ids")?.Add(id);
                }
            }

            if (Dss.TryGetValue(userDescription, out var known))
            {
                ArrayOf(userMap, "ds")?.Add(EncodeDeleteSet(known));
            }
        }));

        Doc.AfterTransaction += (s, tr) =>
        {
            // The transaction is reused once its batch has ended: what the write needs is read now.
            var ds = tr.DeleteSet;
            if (tr.Local && ds.Clients.Count > 0 && filter(tr))
            {
                var encoded = EncodeDeleteSet(ds);
                _deferred.Add(() => ArrayOf(userMap, "ds")?.Add(encoded));
            }
        };
    }

    private void Flush(object? sender, IReadOnlyList<Transaction> transactions)
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
    public string? GetUserByClientId(long clientId) => Clients.TryGetValue(clientId, out var v) ? v : null;

    /// <summary>
    /// Yjs parity: <c>getUserByDeletedId(id)</c>.
    /// </summary>
    public string? GetUserByDeletedId(ItemId id)
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
    private static DeleteSet? TryDecodeDeleteSet(byte[] encoded)
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
