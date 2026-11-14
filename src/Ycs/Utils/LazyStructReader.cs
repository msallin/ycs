// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Ycs
{
    /// <summary>
    /// Reads one struct (<see cref="Item"/>, <see cref="GC"/>, or <see cref="Skip"/>) at a time
    /// from an update, across client boundaries, without materializing the whole update or
    /// integrating anything into a document.
    /// <br/>
    /// Ported from Yjs' <c>LazyStructReader</c> (src/utils/updates.js). Unlike
    /// <see cref="EncodingUtils.ReadClientStructRefs"/> (which is used when actually applying an
    /// update to a live <see cref="YDoc"/>), this reader:
    /// <list type="bullet">
    /// <item>understands the 'Skip' struct (info byte 10), which <c>ReadClientStructRefs</c> does not;</item>
    /// <item>leaves an <see cref="Item"/>'s <c>Parent</c> unresolved (a raw parent key string or a
    /// left-neighbor <see cref="ID"/>) instead of looking it up in a document's shared types, since
    /// there may not be a document at all.</item>
    /// </list>
    /// It is only used by the update-manipulation utilities in <see cref="UpdateOperations"/>.
    /// </summary>
    internal sealed class LazyStructReader
    {
        private readonly IEnumerator<AbstractStruct> _generator;
        private readonly bool _filterSkips;

        public LazyStructReader(IUpdateDecoder decoder, bool filterSkips)
        {
            _generator = Generate(decoder).GetEnumerator();
            _filterSkips = filterSkips;
            Curr = null;

            Next();
        }

        /// <summary>
        /// The struct last read by <see cref="Next"/>, or <c>null</c> once the update is exhausted.
        /// </summary>
        public AbstractStruct Curr { get; private set; }

        /// <summary>
        /// Advances to (and returns) the next struct, skipping over 'Skip' structs when
        /// <c>filterSkips</c> was requested in the constructor.
        /// </summary>
        public AbstractStruct Next()
        {
            do
            {
                Curr = _generator.MoveNext() ? _generator.Current : null;
            }
            while (_filterSkips && Curr != null && Curr is Skip);

            return Curr;
        }

        /// <summary>
        /// Mirrors <see cref="EncodingUtils.ReadClientStructRefs"/>, except it yields one struct at
        /// a time (instead of building the full per-client lists up front) and understands 'Skip'.
        /// </summary>
        private static IEnumerable<AbstractStruct> Generate(IUpdateDecoder decoder)
        {
            var numOfStateUpdates = decoder.Reader.ReadVarUint();

            for (var i = 0; i < numOfStateUpdates; i++)
            {
                var numberOfStructs = decoder.Reader.ReadVarUint();
                long client = decoder.ReadClient();
                long clock = decoder.Reader.ReadVarUint();

                for (var j = 0; j < numberOfStructs; j++)
                {
                    var info = decoder.ReadInfo();

                    if (info == Skip.StructSkipRefNumber)
                    {
                        var len = (int)decoder.Reader.ReadVarUint();
                        var skip = new Skip(new ID(client, clock), len);
                        yield return skip;
                        clock += len;
                    }
                    else if ((Bits.Bits5 & info) != 0)
                    {
                        // The item that was originally to the left/right of this item.
                        var leftOrigin = (info & Bit.Bit8) == Bit.Bit8 ? (ID?)decoder.ReadLeftId() : null;
                        var rightOrigin = (info & Bit.Bit7) == Bit.Bit7 ? (ID?)decoder.ReadRightId() : null;
                        var cantCopyParentInfo = (info & (Bit.Bit7 | Bit.Bit8)) == 0;
                        var hasParentYKey = cantCopyParentInfo && decoder.ReadParentInfo();

                        // Unlike 'ReadClientStructRefs', we do NOT resolve the parent against a
                        // document here - there may not be one. It stays either a parent-key
                        // string or the ID of the parent's defining item.
                        object parent = null;
                        if (cantCopyParentInfo)
                        {
                            parent = hasParentYKey ? (object)decoder.ReadString() : (object)decoder.ReadLeftId();
                        }

                        var parentSub = cantCopyParentInfo && (info & Bit.Bit6) == Bit.Bit6 ? decoder.ReadString() : null;
                        var content = EncodingUtils.ReadItemContent(decoder, info);

                        var item = new Item(
                            new ID(client, clock),
                            null, // left
                            leftOrigin,
                            null, // right
                            rightOrigin,
                            parent,
                            parentSub,
                            content);

                        yield return item;
                        clock += item.Length;
                    }
                    else
                    {
                        var length = decoder.ReadLength();
                        var gc = new GC(new ID(client, clock), length);
                        yield return gc;
                        clock += length;
                    }
                }
            }
        }
    }
}
