// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Diagnostics;

namespace Ycs;

internal abstract partial class AbstractStruct
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
        protected set
        {
            _id = value;
            if (Handle >= 0)
            {
                WriteId();
            }
        }
    }

    public int Length
    {
        get => _length;
        protected set
        {
            _length = value;
            if (Handle >= 0)
            {
                SlotStore.Slab.At(Handle).Length = value;
            }
        }
    }

    public abstract bool Deleted { get; }

    internal abstract bool MergeWith(AbstractStruct right);

    /// <summary>Whether <see cref="MergeWith"/> would merge <paramref name="right"/>, without changing either.</summary>
    internal abstract bool CanMergeWith(AbstractStruct right);
    internal abstract void Write(IUpdateEncoder encoder, int offset);
}
