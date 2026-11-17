#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// Whether the slot was freed (the struct merged into its left neighbour) and not yet reused, or names none, read
    /// without the Debug generation check: for the code that looks for a stale reference.
    /// </summary>
    internal bool IsFreed => !_slab.TryRead(_handle, out var slot) || slot.IsFree;
}
