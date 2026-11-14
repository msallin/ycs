// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Base class of the lib0 column decoders used by the v2 update format.
/// </summary>
internal abstract class AbstractStreamDecoder<T> : IDecoder<T>
{
    protected AbstractStreamDecoder(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reader = input;
    }

    protected Lib0Reader Reader { get; }

    protected bool HasContent => Reader.HasContent;

    public abstract T Read();
}
