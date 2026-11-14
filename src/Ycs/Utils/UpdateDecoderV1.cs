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
    /// Reader for the original ('v1') Yjs update format. See <see cref="DSEncoderV1"/>.
    /// </summary>
    internal class DSDecoderV1 : IDSDecoder
    {
        private readonly bool _leaveOpen;

        public DSDecoderV1(Stream input, bool leaveOpen = false)
        {
            _leaveOpen = leaveOpen;
            Reader = input;
        }

        public Stream Reader { get; private set; }
        protected bool Disposed { get; private set; }

        public void Dispose()
        {
            Dispose(disposing: true);
            System.GC.SuppressFinalize(this);
        }

        public void ResetDsCurVal()
        {
            // Nothing to do.
        }

        public long ReadDsClock()
        {
            CheckDisposed();
            return Reader.ReadVarUint();
        }

        public long ReadDsLength()
        {
            CheckDisposed();
            var length = Reader.ReadVarUint();
            Debug.Assert(length > 0);
            return length;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing && !_leaveOpen)
                {
                    Reader?.Dispose();
                }

                Reader = null;
                Disposed = true;
            }
        }

        protected void CheckDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(DSDecoderV1));
            }
        }
    }

    internal sealed class UpdateDecoderV1 : DSDecoderV1, IUpdateDecoder
    {
        public UpdateDecoderV1(Stream input, bool leaveOpen = false)
            : base(input, leaveOpen)
        {
        }

        public ID ReadLeftId()
        {
            CheckDisposed();
            return new ID(Reader.ReadVarUint(), Reader.ReadVarUint());
        }

        public ID ReadRightId()
        {
            CheckDisposed();
            return new ID(Reader.ReadVarUint(), Reader.ReadVarUint());
        }

        public long ReadClient()
        {
            CheckDisposed();
            return Reader.ReadVarUint();
        }

        public byte ReadInfo()
        {
            CheckDisposed();
            return (byte)Reader.ReadByte();
        }

        public string ReadString()
        {
            CheckDisposed();
            return Reader.ReadVarString();
        }

        public bool ReadParentInfo()
        {
            CheckDisposed();
            return Reader.ReadVarUint() == 1;
        }

        public uint ReadTypeRef()
        {
            CheckDisposed();
            return Reader.ReadVarUint();
        }

        public int ReadLength()
        {
            CheckDisposed();
            var value = (int)Reader.ReadVarUint();
            Debug.Assert(value >= 0);
            return value;
        }

        public object ReadAny()
        {
            CheckDisposed();
            return Reader.ReadAny();
        }

        public byte[] ReadBuffer()
        {
            CheckDisposed();
            return Reader.ReadVarUint8Array();
        }

        public string ReadKey()
        {
            CheckDisposed();
            return Reader.ReadVarString();
        }

        public object ReadJson()
        {
            CheckDisposed();
            var jsonString = Reader.ReadVarString();
            // Plain CLR values: the same content may be re-encoded with the v2 encoder,
            // which writes it through WriteAny.
            return YJson.Parse(jsonString);
        }

    }
}
