// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;

namespace Ycs
{
    /// <summary>
    /// A placeholder struct that represents a range of clock values for which we don't have any
    /// data (yet). It never gets integrated into a document; it only exists transiently while
    /// merging/diffing raw update byte streams (see <see cref="UpdateOperations"/>), where a
    /// client's structs might not be contiguous because we only have part of its history.
    /// </summary>
    public sealed class Skip : AbstractStruct
    {
        internal const byte StructSkipRefNumber = 10;

        internal Skip(ID id, int length)
            : base(id, length)
        {
            // Do nothing.
        }

        public override bool Deleted => true;

        /// <summary>
        /// Lets <see cref="UpdateOperations"/> grow/shrink a Skip in place while it is being
        /// merged with newly-seen structs. <see cref="AbstractStruct.Length"/> only has a
        /// 'protected' setter, so this is exposed here instead.
        /// </summary>
        internal void SetLength(int length)
        {
            System.Diagnostics.Debug.Assert(length >= 0);
            Length = length;
        }

        internal override bool MergeWith(AbstractStruct right)
        {
            if (!(right is Skip))
            {
                return false;
            }

            Length += right.Length;
            return true;
        }

        internal override void Delete(Transaction transaction)
        {
            // Do nothing.
        }

        internal override void Integrate(Transaction transaction, int offset)
        {
            // Skip structs cannot be integrated.
            throw new NotSupportedException("Skip structs cannot be integrated.");
        }

        internal override long? GetMissing(Transaction transaction, StructStore store)
        {
            return null;
        }

        internal override void Write(IUpdateEncoder encoder, int offset)
        {
            encoder.WriteInfo(StructSkipRefNumber);

            // Write as VarUint because Skips can't make use of predictable length-encoding.
            encoder.RestWriter.WriteVarUint((uint)(Length - offset));
        }
    }
}
