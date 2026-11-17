#nullable enable

namespace Ycs;

/// <summary>
/// Which side of a position's neighbours a <see cref="RelativePosition"/> sticks to when text is inserted at it.
/// The values are those Yjs writes. A peer's <c>assoc</c> may be any integer and is kept as it is: a negative one means
/// <see cref="Before"/>, any other <see cref="After"/>.
/// </summary>
public enum Assoc
{
    /// <summary>The position is associated with the item after it (position 1 in <c>'ab'</c> with <c>'b'</c>): inserted text goes before it.</summary>
    After = 0,

    /// <summary>The position is associated with the item before it (position 1 in <c>'ab'</c> with <c>'a'</c>): inserted text goes after it.</summary>
    Before = -1,
}
