// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Base class of the lib0 column encoders used by the v2 update format. Each one writes
/// into its own <see cref="Lib0Writer"/>; <see cref="GetWrittenSpan"/> flushes any pending
/// run and returns the bytes.
/// </summary>
internal abstract class AbstractStreamEncoder<T> : IEncoder<T>
{
    protected AbstractStreamEncoder()
    {
        Writer = new Lib0Writer();
    }

    protected Lib0Writer Writer { get; }

    public abstract void Write(T value);

    /// <summary>
    /// Flushes the pending run and returns the encoded bytes. Call once, after the last write.
    /// </summary>
    public ReadOnlySpan<byte> GetWrittenSpan()
    {
        Flush();
        return Writer.WrittenSpan;
    }

    public byte[] ToArray() => GetWrittenSpan().ToArray();

    protected virtual void Flush()
    {
        // Do nothing.
    }
}
