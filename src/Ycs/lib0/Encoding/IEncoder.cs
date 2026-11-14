// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

internal interface IEncoder<T>
{
    void Write(T value);

    /// <summary>
    /// Flushes the pending run and returns the encoded bytes. Call once, after the last write.
    /// </summary>
    ReadOnlySpan<byte> GetWrittenSpan();
}
