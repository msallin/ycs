#nullable enable

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Ycs;

/// <summary>
/// A position in a shared type that survives edits: placed before a character, it keeps pointing at that
/// character; placed at the end of a type, it keeps pointing at the end. Exactly one of <see cref="Item"/>,
/// <see cref="RootName"/> and <see cref="TypeId"/> locates it (<see cref="Item"/> wins in the encoding, then
/// <see cref="RootName"/>); <c>default</c> locates nothing and neither encodes nor resolves.
/// <br/>
/// The binary form and the JSON form are those of Yjs' <c>encodeRelativePosition</c> and
/// <c>relativePositionToJSON</c>, so a position travels between peers (y-protocols' awareness carries one per
/// cursor).
/// </summary>
/// <param name="TypeId">The id of the type that holds the position, when that type is not a root type.</param>
/// <param name="RootName">The name of the root type that holds the position.</param>
/// <param name="Item">The id of the character the position is associated with.</param>
/// <param name="Assoc">The side the position sticks to.</param>
public readonly record struct RelativePosition(ItemId? TypeId, string? RootName, ItemId? Item, Assoc Assoc = Assoc.After)
{
    /// <summary>
    /// The position of <paramref name="index"/> in <paramref name="type"/>. It carries only what the binary form
    /// does (as Yjs' <c>createRelativePositionFromTypeIndex</c> sets all three but encodes one), so it equals what
    /// <see cref="Decode"/> reads back from <see cref="Encode()"/>: the item when there is one, else the root type's
    /// name, else the id of the type.
    /// </summary>
    public static RelativePosition FromIndex(AbstractType type, int index, Assoc assoc = Assoc.After)
    {
        ArgumentNullException.ThrowIfNull(type);

        if ((int)assoc < 0)
        {
            // Associated with the left character or the beginning of a type, decrement index if possible.
            if (index == 0)
            {
                return In(type, null, assoc);
            }

            index--;
        }

        var t = type._start;
        while (!t.IsNull)
        {
            ref readonly var slot = ref t.Peek();
            if ((slot.Info & (Slot.DeletedFlag | Slot.CountableFlag)) == Slot.CountableFlag)
            {
                if (slot.Length > index)
                {
                    // Case 1: found position somewhere in the linked list.
                    return In(type, new ItemId(t.Id.Client, slot.Clock + index), assoc);
                }

                index -= slot.Length;
            }

            if (slot.Right < 0 && (int)assoc < 0)
            {
                // Left-associated position, return last available id.
                return In(type, t.LastId, assoc);
            }

            t = t.Right.AsItem;
        }

        return In(type, null, assoc);
    }

    /// <summary>
    /// The index in <paramref name="doc"/> this position is at now; <c>false</c> when the position names a
    /// struct the document does not have (yet) or has garbage collected.
    /// <br/>
    /// To share the position with other users, pass <paramref name="followUndoneDeletions"/> <c>false</c> to get
    /// the same result on every client: by default an undone deletion is followed to the content that recreated
    /// it, which is better for the user who undid but only for that user. See
    /// https://github.com/yjs/yjs/issues/638.
    /// </summary>
    public bool TryResolve(YDoc doc, out AbsolutePosition position, bool followUndoneDeletions = true)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return AbsolutePosition.TryResolve(this, doc, followUndoneDeletions, out position);
    }

    /// <summary>The bytes of Yjs' <c>encodeRelativePosition</c>.</summary>
    /// <exception cref="InvalidOperationException">The position is <c>default</c>: it locates nothing.</exception>
    public byte[] Encode()
    {
        var writer = new Lib0Writer();
        Write(writer);
        return writer.ToArray();
    }

    /// <summary><see cref="Encode()"/> into <paramref name="destination"/>.</summary>
    public void Encode(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var writer = new Lib0Writer();
        Write(writer);
        writer.CopyTo(destination);
    }

    /// <summary>Reads the bytes of Yjs' <c>encodeRelativePosition</c>.</summary>
    /// <exception cref="YUpdateException">The bytes are truncated or name an unknown kind of position.</exception>
    public static RelativePosition Decode(ReadOnlySpan<byte> encoded) => Read(new Lib0Reader(encoded.ToArray()));

    /// <summary>
    /// Writes Yjs' <c>relativePositionToJSON</c> object: <c>type</c>, <c>tname</c> and <c>item</c> only where set
    /// (Yjs leaves out an empty <c>tname</c>, so its JSON of a position at the end of the root <c>""</c> cannot
    /// be resolved; this keeps it), then <c>assoc</c>.
    /// </summary>
    public void WriteJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        if (TypeId is { } typeId)
        {
            WriteJsonId(writer, "type", typeId);
        }

        if (RootName != null)
        {
            writer.WriteString("tname", RootName);
        }

        if (Item is { } item)
        {
            WriteJsonId(writer, "item", item);
        }

        writer.WriteNumber("assoc", (int)Assoc);
        writer.WriteEndObject();
    }

    /// <summary>The JSON text of <see cref="WriteJson"/>, for a peer's awareness state.</summary>
    public string ToJsonString()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteJson(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary><see cref="FromJson(JsonElement)"/> of JSON text.</summary>
    /// <exception cref="YUpdateException">As <see cref="FromJson(JsonElement)"/>, or the text is not JSON.</exception>
    public static RelativePosition FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return FromJson(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new YUpdateException("A relative position is not valid JSON.", e);
        }
    }

    /// <summary>
    /// Reads the object of Yjs' <c>relativePositionToJSON</c>. The JSON may come from another peer: <c>null</c>
    /// members count as absent; <c>assoc</c> keeps its value, as Yjs does (a negative one means
    /// <see cref="Assoc.Before"/>, any other <see cref="Assoc.After"/>).
    /// </summary>
    /// <exception cref="YUpdateException">
    /// Not an object; an id that is not an object of two integers from 0 to 2^53 - 1; an <c>assoc</c> that is
    /// not an integer; a <c>tname</c> that is not a string; none of <c>item</c>, <c>type</c> and <c>tname</c>.
    /// </exception>
    public static RelativePosition FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new YUpdateException("A relative position is not a JSON object.");
        }

        ItemId? typeId = Member(json, "type", out var type) ? JsonToId(type, "type") : null;
        string? rootName = Member(json, "tname", out var tname)
            ? tname.ValueKind == JsonValueKind.String ? tname.GetString() : throw new YUpdateException("The tname of a relative position is not a string.")
            : null;
        ItemId? item = Member(json, "item", out var itemJson) ? JsonToId(itemJson, "item") : null;
        var assoc = Member(json, "assoc", out var assocJson) ? AssocOf(JsonInteger(assocJson, "assoc")) : Assoc.After;

        if (item == null && rootName == null && typeId == null)
        {
            throw new YUpdateException("A relative position names neither an item, a type nor a root type.");
        }

        return new RelativePosition(typeId, rootName, item, assoc);
    }

    private static RelativePosition In(AbstractType type, ItemId? item, Assoc assoc)
    {
        if (item != null)
        {
            return new RelativePosition(null, null, item, assoc);
        }

        if (type.IsRoot)
        {
            return new RelativePosition(null, type.FindRootTypeKey(), null, assoc);
        }

        var id = type.ItemId;
        return new RelativePosition(new ItemId(id.Client, id.Clock), null, null, assoc);
    }

    private static void WriteJsonId(Utf8JsonWriter writer, string name, ItemId id)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("client", id.Client);
        writer.WriteNumber("clock", id.Clock);
        writer.WriteEndObject();
    }

    private static bool Member(JsonElement json, string name, out JsonElement value) =>
        json.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static ItemId JsonToId(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("client", out var client) || !json.TryGetProperty("clock", out var clock))
        {
            throw new YUpdateException($"The {name} of a relative position is not an object with a client and a clock.");
        }

        var clientValue = JsonInteger(client, name + ".client");
        var clockValue = JsonInteger(clock, name + ".clock");
        if (clientValue < 0 || clientValue > JsValue.MaxSafeInteger || clockValue < 0 || clockValue > JsValue.MaxSafeInteger)
        {
            throw new YUpdateException($"The {name} of a relative position is out of range (0 to 2^53 - 1).");
        }

        return new ItemId(clientValue, clockValue);
    }

    private static long JsonInteger(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var integer))
            {
                return integer;
            }

            if (JsValue.IsInteger(value.GetDouble(), out integer))
            {
                return integer;
            }
        }

        throw new YUpdateException($"The {name} of a relative position is not an integer.");
    }

    internal void Write(Lib0Writer writer)
    {
        if (Item != null)
        {
            // Case 1: Found position somewhere in the linked list.
            writer.WriteVarUint(0);
            Item.Value.Write(writer);
        }
        else if (RootName != null)
        {
            // Case 2: Found position at the end of the list and type is stored in y.share.
            writer.WriteVarUint(1);
            writer.WriteVarString(RootName);
        }
        else if (TypeId != null)
        {
            // Case 3: Found position at the end of the list and type is attached to an item.
            writer.WriteVarUint(2);
            TypeId.Value.Write(writer);
        }
        else
        {
            throw new InvalidOperationException("A default RelativePosition locates nothing: it has no item, type or root type to encode.");
        }

        writer.WriteVarInt((int)Assoc);
    }

    internal static RelativePosition Read(Lib0Reader reader)
    {
        ItemId? itemId = null;
        ItemId? typeId = null;
        string? rootName = null;

        switch (reader.ReadVarUint())
        {
            case 0:
                // Case 1: Found position somewhere in the linked list.
                itemId = ItemId.Read(reader);
                break;
            case 1:
                // Case 2: Found position at the end of the list and type is stored in y.share.
                rootName = reader.ReadVarString();
                break;
            case 2:
                // Case 3: Found position at the end of the list and type is attached to an item.
                typeId = ItemId.Read(reader);
                break;
            default:
                throw new YUpdateException("Unknown kind of relative position.");
        }

        var assoc = reader.HasContent ? AssocOf(reader.ReadVarInt().Value) : Assoc.After;
        return new RelativePosition(typeId, rootName, itemId, assoc);
    }

    // Yjs keeps a peer's assoc as it is and writes it back unchanged, so the value is kept (its sign is its meaning); one
    // beyond an int keeps its sign.
    private static Assoc AssocOf(long value) => (Assoc)(int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
