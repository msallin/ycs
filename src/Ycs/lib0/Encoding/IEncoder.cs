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
    /// Flushes the pending run (the first time) and returns the encoded bytes.
    /// </summary>
    ReadOnlySpan<byte> GetWrittenSpan();
}
