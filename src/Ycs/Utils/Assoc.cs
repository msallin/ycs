#nullable enable

namespace Ycs;

/// <summary>
/// Which side of a position's neighbours a <see cref="RelativePosition"/> sticks to when text is inserted at it.
/// The values are those Yjs writes (its <c>assoc</c> is any integer; a peer's negative one reads as
/// <see cref="Before"/>, any other as <see cref="After"/>).
/// </summary>
public enum Assoc
{
    /// <summary>The position is associated with the item after it (position 1 in <c>'ab'</c> with <c>'b'</c>): inserted text goes before it.</summary>
    After = 0,

    /// <summary>The position is associated with the item before it (position 1 in <c>'ab'</c> with <c>'a'</c>): inserted text goes after it.</summary>
    Before = -1,
}
