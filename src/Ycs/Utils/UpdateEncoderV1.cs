// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;

namespace Ycs
{
    /// <summary>
    /// The original ('v1') Yjs update format: everything is written to a single stream
    /// as plain var-ints. It is the format used by <c>Y.encodeStateAsUpdate</c>,
    /// <c>y-protocols/sync</c> and every provider built on them, so it is the one
    /// on the wire in practice; <see cref="DSEncoderV2"/> is the newer, denser variant.
    /// </summary>
    internal class DSEncoderV1 : IDSEncoder
    {
        public DSEncoderV1()
        {
            RestWriter = new MemoryStream();
        }

        public Stream RestWriter { get; private set; }
        protected bool Disposed { get; private set; }

        public void Dispose()
        {
            Dispose(disposing: true);
            System.GC.SuppressFinalize(this);
        }

        // ponytail: v1 writes absolute clocks, so there is no running diff to reset.
        public void ResetDsCurVal()
        {
            // Nothing to do.
        }

        public void WriteDsClock(long clock)
        {
            Debug.Assert(clock >= 0);
            RestWriter.WriteVarUint((uint)clock);
        }

        public void WriteDsLength(long length)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            RestWriter.WriteVarUint((uint)length);
        }

        public virtual byte[] ToArray()
        {
            return ((MemoryStream)RestWriter).ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    RestWriter.Dispose();
                }

                RestWriter = null;
                Disposed = true;
            }
        }
    }

    internal sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
    {
        public void WriteLeftId(ID id)
        {
            RestWriter.WriteVarUint((uint)id.Client);
            RestWriter.WriteVarUint((uint)id.Clock);
        }

        public void WriteRightId(ID id)
        {
            RestWriter.WriteVarUint((uint)id.Client);
            RestWriter.WriteVarUint((uint)id.Clock);
        }

        public void WriteClient(long client)
        {
            Debug.Assert(client >= 0);
            RestWriter.WriteVarUint((uint)client);
        }

        public void WriteInfo(byte info)
        {
            RestWriter.WriteByte(info);
        }

        public void WriteString(string s)
        {
            RestWriter.WriteVarString(s);
        }

        public void WriteParentInfo(bool isYKey)
        {
            RestWriter.WriteVarUint(isYKey ? 1u : 0u);
        }

        public void WriteTypeRef(uint info)
        {
            RestWriter.WriteVarUint(info);
        }

        public void WriteLength(int len)
        {
            Debug.Assert(len >= 0);
            RestWriter.WriteVarUint((uint)len);
        }

        public void WriteAny(object any)
        {
            RestWriter.WriteAny(any);
        }

        public void WriteBuffer(byte[] data)
        {
            RestWriter.WriteVarUint8Array(data);
        }

        public void WriteKey(string key)
        {
            RestWriter.WriteVarString(key);
        }

        public void WriteJson<T>(T any)
        {
            var jsonString = YJson.Stringify(any);
            RestWriter.WriteVarString(jsonString);
        }
    }
}
