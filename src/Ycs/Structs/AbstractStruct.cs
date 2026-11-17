using System.Diagnostics;

namespace Ycs;

/// <summary>
/// A struct as decoded from an update: in no store and without neighbours. The structs of a store are slots
/// (<see cref="ItemRef"/>); a decoded one enters a store through <see cref="ItemRef.Adopt"/>.
/// </summary>
internal abstract class AbstractStruct
{
    protected AbstractStruct(ID id, int length)
    {
        Debug.Assert(length >= 0);

        Id = id;
        Length = length;
    }

    private ID _id;
    private int _length;

    public ID Id
    {
        get => _id;
        protected set => _id = value;
    }

    public int Length
    {
        get => _length;
        protected set => _length = value;
    }

    public abstract bool Deleted { get; }

    /// <summary>The last content address of this struct.</summary>
    public ID LastId => Length == 1 ? Id : new ID(Id.Client, Id.Clock + Length - 1);

    internal abstract bool MergeWith(AbstractStruct right);

    /// <summary>Whether <see cref="MergeWith"/> would merge <paramref name="right"/>, without changing either.</summary>
    internal abstract bool CanMergeWith(AbstractStruct right);
    internal abstract void Write(IUpdateEncoder encoder, int offset);
}
