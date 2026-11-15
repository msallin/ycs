// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// A relative position is based on the YUjs model and is not affected by document changes.
/// E.g. if you place a relative position before a certain character, it will always point to this character.
/// If you place a relative position at the end of a type, it will always point to the end of the type.
/// <br/>
/// A numberic position is often unsuited for user selections, because it does not change when content is inserted
/// before or after.
/// <br/>
/// <c>Insert(0, 'x')('a|bc') = 'xa|bc'</c> Where | is tehre relative position.
/// <br/>
/// Only one property must be defined.
/// <br/>
/// This type mirrors Yjs 13.6.32's <c>RelativePosition</c> (src/utils/RelativePosition.js). It is
/// public - rather than the <c>internal</c> it started as - because it is the basis for the
/// shared-cursor / awareness use case (y-protocols' awareness carries an encoded
/// <see cref="RelativePosition"/> per peer), which is unreachable from outside this assembly
/// otherwise.
/// </summary>
public partial class RelativePosition : IEquatable<RelativePosition>
{
    public readonly ID? Item;
    public readonly ID? TypeId;
    public readonly string TName;

    /// <summary>
    /// A relative position is associated to a specific character.
    /// By default, the value is <c>&gt;&eq; 0</c>, the relative position is associated to the character
    /// after the meant position.
    /// I.e. position <c>1</c> in <c>'ab'</c> is associated with the character <c>'b'</c>.
    /// <br/>
    /// If the value is <c>&lt; 0</c>, then the relative position is associated with the caharacter
    /// before the meant position.
    /// </summary>
    public int Assoc;

    public RelativePosition(AbstractType type, ID? item, int assoc = 0)
    {
        Item = item;
        Assoc = assoc;

        if (type._item == null)
        {
            TName = type.FindRootTypeKey();
        }
        else
        {
            TypeId = new ID(type._item.Id.Client, type._item.Id.Clock);
        }
    }

    private RelativePosition(ID? typeId, string tname, ID? item, int assoc)
    {
        TypeId = typeId;
        TName = tname;
        Item = item;
        Assoc = assoc;
    }

    public bool Equals(RelativePosition other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other != null
            && string.Equals(TName, other.TName)
            && ID.Equals(Item, other.Item)
            && ID.Equals(TypeId, other.TypeId)
            && Assoc == other.Assoc;
    }

    /// <summary>
    /// Create a relative position based on an absolute position.
    /// </summary>
    public static RelativePosition FromTypeIndex(AbstractType type, int index, int assoc = 0)
    {
        if (assoc < 0)
        {
            // Associated with the left character or the beginning of a type, decrement index if possible.
            if (index == 0)
            {
                return new RelativePosition(type, null, assoc);
            }

            index--;
        }

        var t = type._start;
        while (t != null)
        {
            if (!t.Deleted && t.Countable)
            {
                if (t.Length > index)
                {
                    // Case 1: found position somewhere in the linked list.
                    return new RelativePosition(type, new ID(t.Id.Client, t.Id.Clock + index), assoc);
                }

                index -= t.Length;
            }

            if (t.Right == null && assoc < 0)
            {
                // Left-associated position, return last available id.
                return new RelativePosition(type, t.LastId, assoc);
            }

            t = t.Right as Item;
        }

        return new RelativePosition(type, null, assoc);
    }

    /// <summary>
    /// Yjs parity: <c>createRelativePositionFromTypeIndex(type, index, assoc)</c>. Alias for
    /// <see cref="FromTypeIndex"/>, kept under the Yjs name for discoverability.
    /// </summary>
    public static RelativePosition CreateRelativePositionFromTypeIndex(AbstractType type, int index, int assoc = 0)
        => FromTypeIndex(type, index, assoc);

    /// <summary>
    /// Yjs parity: <c>compareRelativePositions(a, b)</c>.
    /// </summary>
    public static bool CompareRelativePositions(RelativePosition a, RelativePosition b)
    {
        return ReferenceEquals(a, b) || (a != null && b != null && a.Equals(b));
    }

    /// <summary>
    /// Yjs parity: <c>relativePositionToJSON(rpos)</c>. Mirrors the plain-object shape Yjs produces:
    /// only the properties that are actually set are present in the result.
    /// </summary>
    public IDictionary<string, object> RelativePositionToJSON()
    {
        var json = new Dictionary<string, object>();

        if (TypeId != null)
        {
            json["type"] = IdToJson(TypeId.Value);
        }

        // Yjs leaves out an empty tname, so its JSON of a position at the end of the root "" cannot be
        // resolved; this keeps it.
        if (TName != null)
        {
            json["tname"] = TName;
        }

        if (Item != null)
        {
            json["item"] = IdToJson(Item.Value);
        }

        json["assoc"] = Assoc;

        return json;
    }

    /// <summary>
    /// Yjs parity: <c>createRelativePositionFromJSON(json)</c>. The JSON may come from another peer: a
    /// malformed one (an id that is not an object of two integers from 0 to 2^53 - 1, an <c>assoc</c>
    /// that is not an integer, neither <c>item</c>, <c>type</c> nor <c>tname</c>) throws
    /// <see cref="YUpdateException"/>. <c>assoc</c> is clamped to the range of an <see cref="int"/>.
    /// </summary>
    public static RelativePosition CreateRelativePositionFromJSON(IDictionary<string, object> json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ID? typeId = json.TryGetValue("type", out var type) && type != null ? JsonToId(type, "type") : null;
        string tname = json.TryGetValue("tname", out var tn) && tn != null
            ? tn as string ?? throw new YUpdateException("The tname of a relative position is not a string.")
            : null;
        ID? item = json.TryGetValue("item", out var it) && it != null ? JsonToId(it, "item") : null;
        int assoc = json.TryGetValue("assoc", out var a) && a != null ? (int)Math.Clamp(JsonInteger(a, "assoc"), int.MinValue, int.MaxValue) : 0;

        if (item == null && tname == null && typeId == null)
        {
            throw new YUpdateException("A relative position names neither an item, a type nor a root type.");
        }

        return new RelativePosition(typeId, tname, item, assoc);
    }

    private static IDictionary<string, object> IdToJson(ID id) => new Dictionary<string, object>
    {
        ["client"] = id.Client,
        ["clock"] = id.Clock
    };

    private static ID JsonToId(object json, string name)
    {
        if (json is not IDictionary<string, object> dict
            || !dict.TryGetValue("client", out var client) || !dict.TryGetValue("clock", out var clock))
        {
            throw new YUpdateException($"The {name} of a relative position is not an object with a client and a clock.");
        }

        var clientValue = JsonInteger(client, name + ".client");
        var clockValue = JsonInteger(clock, name + ".clock");
        if (clientValue < 0 || clientValue > JsValue.MaxSafeInteger || clockValue < 0 || clockValue > JsValue.MaxSafeInteger)
        {
            throw new YUpdateException($"The {name} of a relative position is out of range (0 to 2^53 - 1).");
        }

        return new ID(clientValue, clockValue);
    }

    private static long JsonInteger(object value, string name) =>
        JsValue.IsInteger(value, out var integer) ? integer : throw new YUpdateException($"The {name} of a relative position is not an integer.");

    internal void Write(Lib0Writer writer)
    {
        if (Item != null)
        {
            // Case 1: Found position somewhere in the linked list.
            writer.WriteVarUint(0);
            Item.Value.Write(writer);
        }
        else if (TName != null)
        {
            // Case 2: Found position at the end of the list and type is stored in y.share.
            writer.WriteVarUint(1);
            writer.WriteVarString(TName);
        }
        else
        {
            // Case 3: Found position at the end of the list and type is attached to an item.
            // Every constructor sets one of the three.
            writer.WriteVarUint(2);
            TypeId.Value.Write(writer);
        }

        writer.WriteVarInt(Assoc);
    }

    public static RelativePosition Read(byte[] encodedPosition)
    {
        return Read(new Lib0Reader(encodedPosition));
    }

    internal static RelativePosition Read(Lib0Reader reader)
    {
        ID? itemId = null;
        ID? typeId = null;
        string tName = null;

        switch (reader.ReadVarUint())
        {
            case 0:
                // Case 1: Found position somewhere in the linked list.
                itemId = ID.Read(reader);
                break;
            case 1:
                // Case 2: Found position at the end of the list and type is stored in y.share.
                tName = reader.ReadVarString();
                break;
            case 2:
                // Case 3: Found position at the end of the list and type is attached to an item.
                typeId = ID.Read(reader);
                break;
            default:
                throw new YUpdateException("Unknown kind of relative position.");
        }

        var assoc = reader.HasContent ? (int)Math.Clamp(reader.ReadVarInt().Value, int.MinValue, int.MaxValue) : 0;
        return new RelativePosition(typeId, tName, itemId, assoc);
    }

    public byte[] ToArray()
    {
        var writer = new Lib0Writer();
        Write(writer);
        return writer.ToArray();
    }

    /// <summary>
    /// Yjs parity: <c>encodeRelativePosition(rpos)</c>. Alias for <see cref="ToArray"/> - this is the
    /// binary form used by y-protocols' awareness.
    /// </summary>
    public static byte[] EncodeRelativePosition(RelativePosition rpos) => rpos.ToArray();

    /// <summary>
    /// Yjs parity: <c>decodeRelativePosition(uint8Array)</c>. Alias for <see cref="Read(byte[])"/>.
    /// </summary>
    public static RelativePosition DecodeRelativePosition(byte[] encoded) => Read(encoded);
}
