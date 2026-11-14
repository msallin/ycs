// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Base class of the lib0 column decoders used by the v2 update format. A decoder is reused by
/// pointing <see cref="Input"/> at another column and calling <see cref="Reset"/>.
/// </summary>
internal abstract class AbstractStreamDecoder<T> : IDecoder<T>
{
    protected AbstractStreamDecoder(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reader = input;
    }

    /// <summary>The column this decoder reads.</summary>
    public Lib0Reader Input => Reader;

    protected Lib0Reader Reader { get; }

    /// <summary>Starts over at the current position of <see cref="Input"/>.</summary>
    public abstract void Reset();

    protected bool HasContent => Reader.HasContent;

    public abstract T Read();
}
