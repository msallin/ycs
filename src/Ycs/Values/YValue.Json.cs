using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ycs;

public readonly partial struct YValue
{
    /// <summary>
    /// Writes the value as JSON, as <c>JSON.stringify</c> would write what Yjs' <c>toJSON()</c> returns: null, a boolean, a
    /// number (<c>1.0</c> is <c>1</c>, <c>1e21</c> is <c>1e+21</c>; NaN and the infinities are <c>null</c>), a string, an array, an
    /// object in key order, a shared type as its <see cref="YType.WriteTo"/> (a text as a string, a map as an object ...) and a
    /// subdocument as an object of its root types, as <c>Doc.toJSON()</c>. The two values <c>JSON.stringify</c> cannot write have a
    /// form of their own: a <see cref="YValueKind.BigInteger"/> is a JSON number of its digits (a JavaScript reader loses what
    /// a double cannot hold) and <see cref="YValueKind.Bytes"/> are a base64 string, as <see cref="Utf8JsonWriter.WriteBase64StringValue"/> writes them
    /// (<c>JSON.stringify</c> writes a <c>Uint8Array</c> as an object of its indices). A string with a lone surrogate is written
    /// with the <c>\udXXX</c> escape, a property name with one with U+FFFD.
    /// </summary>
    /// <exception cref="InvalidOperationException">A shared type in the value is a root the document has not been asked for by its kind yet.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValueJson.Write(writer, _kind == YValueKind.SharedType ? ((YType)_object!).ToJsonTree() : Stored());
    }

    /// <summary>
    /// The JSON text of the value, as <see cref="WriteTo"/> writes it. Without an encoder in <paramref name="options"/> the strings are escaped by
    /// <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (<c>&lt;</c> and non-ASCII letters stay, a character beyond the
    /// basic plane is escaped), and the depth is not limited.
    /// </summary>
    public string ToJsonString(JsonWriterOptions options = default) =>
        ValueJson.ToString(_kind == YValueKind.SharedType ? ((YType)_object!).ToJsonTree() : Stored(), options);

    /// <summary>
    /// The value of a JSON value: null, a boolean, a number (always a double: <c>1.0</c> and <c>1</c> are the same value, and a
    /// number beyond a double's precision is rounded), a string, an array, an object (a repeated name keeps its first place and the last value).
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="element"/> has no value (<c>default(JsonElement)</c>).</exception>
    /// <exception cref="JsonException">The element is nested deeper than <see cref="UpdateLimits.DefaultMaxDepth"/>.</exception>
    public static YValue FromJson(JsonElement element) => Parse(element.GetRawText());

    /// <summary>The value of a JSON node as <see cref="FromJson(JsonElement)"/>; null is <see cref="Null"/>.</summary>
    /// <exception cref="JsonException">The node is nested deeper than <see cref="UpdateLimits.DefaultMaxDepth"/>.</exception>
    public static YValue FromJson(JsonNode? node) => node == null ? default : Parse(node.ToJsonString());

    /// <summary>The value of JSON text, as <see cref="FromJson(JsonElement)"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">The text is not one JSON value, or is nested deeper than <see cref="UpdateLimits.DefaultMaxDepth"/>.</exception>
    public static YValue Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromStored(YJson.ParseCore(System.Text.Encoding.UTF8.GetBytes(json), UpdateLimits.DefaultMaxDepth));
    }
}
