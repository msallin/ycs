using System;

namespace Ycs;

/// <summary>
/// Sets a static test switch for the life of a <c>using</c> and puts back the value it had, whatever the test did, so
/// a run mode that sets the switch for the whole run (YCS_POISON, YCS_LEAF_CAPACITY, YCS_SLAB_FIRST) stays set.
/// <c>using var _ = StaticScope.Set(ref TestHooks.SlabFirstPageSlots, 1);</c>
/// </summary>
internal static class StaticScope
{
    public static Scope<T> Set<T>(ref T field, T value)
    {
        var scope = new Scope<T>(ref field);
        field = value;
        return scope;
    }

    public ref struct Scope<T>
    {
        private readonly ref T _field;
        private readonly T _before;

        public Scope(ref T field)
        {
            _field = ref field;
            _before = field;
        }

        public readonly void Dispose() => _field = _before;
    }
}
