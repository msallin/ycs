
using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// A holder's name for a struct of one store: the slot's handle, -1 for none; the slab is the holder's owner's. In
/// Release it is the int and nothing else (a test asserts the size); in Debug it keeps the generation of the slot
/// when it was made, and <see cref="ItemRef"/> built from it checks it on every read, so a holder that outlived its
/// struct throws. A holder never names a freed slot (<see cref="StructStore.CheckForTests"/>).
/// </summary>
internal readonly struct ItemHandle
{
    private readonly int _handle;
#if DEBUG
    private readonly int _generation;
#endif

#if DEBUG
    internal ItemHandle(int handle, int generation)
    {
        _handle = handle;
        _generation = generation;
    }

    internal int Generation => _generation;
#else
    internal ItemHandle(int handle)
    {
        _handle = handle;
    }
#endif

    internal static ItemHandle None
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if DEBUG
        get => new(-1, 0);
#else
        get => new(-1);
#endif
    }

    internal bool IsNone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _handle < 0;
    }

    /// <summary>The slot's handle, -1 for none.</summary>
    internal int Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _handle;
    }

    /// <summary>The reference to the struct in <paramref name="slab"/>, <c>default</c> for none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ItemRef In(Slab slab) => _handle < 0 ? default : ItemRef.Held(slab, this);
}
