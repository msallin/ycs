using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ycs.Client;

/// <summary>A user as another client's awareness state names it.</summary>
/// <param name="ClientId">That client's awareness id.</param>
/// <param name="Name">The name to show.</param>
/// <param name="Colour">The colour, <c>#rrggbb</c> in lower case.</param>
internal sealed record PresenceUser(ulong ClientId, string Name, string Colour);

/// <summary>
/// Who is in the room, as the awareness states of the Yjs editor bindings (y-codemirror, y-quill, y-prosemirror) carry
/// it: <c>{"user":{"name":…,"color":"#rrggbb"}}</c>, other members of the state and of <c>user</c> ignored.
/// </summary>
internal static class Presence
{
    /// <summary>The longest name taken from another client, in UTF-16 code units; a longer one is refused.</summary>
    public const int MaxNameLength = 50;

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    /// <summary>The awareness state of a user with this name and colour.</summary>
    public static string State(string name, string colour)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("user");
            writer.WriteString("name", name);
            writer.WriteString("color", colour);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A random guest: a name with four hex digits and a random colour.</summary>
    public static string GuestState() =>
        State("guest " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2)), "#" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)));

    /// <summary>
    /// Reads a user from an awareness state: <c>user</c> an object with a <c>name</c> of at most
    /// <see cref="MaxNameLength"/> code units and a <c>color</c> <c>#rrggbb</c>; null for anything else. Never throws.
    /// </summary>
    public static PresenceUser? Parse(ulong clientId, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && user.TryGetProperty("color", out var colour) && colour.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: <= MaxNameLength } nameText
                && colour.GetString() is { Length: 7 } colourText && colourText[0] == '#' && !colourText.AsSpan(1).ContainsAnyExcept(HexDigits))
            {
                return new PresenceUser(clientId, nameText, colourText.ToLowerInvariant());
            }

            return null;
        }
        catch (JsonException)
        {
            // Not JSON.
            return null;
        }
        catch (InvalidOperationException)
        {
            // A string JSON allows and UTF-16 cannot hold, such as a lone surrogate.
            return null;
        }
    }

    /// <summary>The users of the remote states that name one, in the order given.</summary>
    public static IReadOnlyList<PresenceUser> Users(IEnumerable<(ulong ClientId, string State)> states) =>
        [.. states.Select(static s => Parse(s.ClientId, s.State)).OfType<PresenceUser>()];
}
