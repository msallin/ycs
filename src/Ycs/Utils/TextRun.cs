#nullable enable

namespace Ycs;

/// <summary>
/// One run of a text as <see cref="YText.ToRuns"/> reads it: what a <see cref="Delta.Insert"/> of <see cref="YText.ToDelta()"/> is, without
/// the characters.
/// </summary>
/// <param name="Length">
/// The run's size in the index space of <see cref="YText.Length"/>: the UTF-16 length of a text run, 1 for an embed or a nested type.
/// </param>
/// <param name="Attributes">The attributes in effect, with <c>ychange</c> where a snapshot asked for it, as the delta has them; null for none.</param>
/// <param name="Embed">The embed's value (a nested type or a value; <see cref="YValue.Null"/> for an embed of null); null for a text run.</param>
public readonly record struct TextRun(int Length, YAttributes? Attributes, YValue? Embed)
{
    /// <summary>Whether the run is one embed or nested type, not text.</summary>
    public bool IsEmbed => Embed.HasValue;
}
