using System;
using System.Buffers;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Buffers that held document content leave the shared array pool cleared.</summary>
[TestClass]
public class PooledBufferClearingTests
{
    private const string Secret = "SECRET-DOCUMENT-CONTENT-SECRET-DOCUMENT-CONTENT";

    private static byte[] SecretBytes => System.Text.Encoding.UTF8.GetBytes(Secret);

    private static bool Holds(byte[] array) => array.AsSpan().IndexOf(SecretBytes) >= 0;

    [TestMethod]
    public void BufferPoolReturn_WithAUsedCount_ClearsThatPrefix()
    {
        var array = BufferPool<byte>.Rent(64);
        array.AsSpan(0, 40).Fill(0x5A);

        BufferPool<byte>.Return(array, 40);

        Assert.IsFalse(array.Contains((byte)0x5A));
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void EncodeStateAsUpdate_ThenRenting_FindsNoContentInTheBucket(UpdateFormat format)
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, Secret);
        var size = doc.EncodeStateAsUpdate(format: format).Length;

        var rented = ArrayPool<byte>.Shared.Rent(size);
        var holds = Holds(rented);
        ArrayPool<byte>.Shared.Return(rented);

        Assert.IsFalse(holds);
    }

    [TestMethod]
    public void StagingTableReturn_AfterRoomWasWritten_KeepsNoCharacters()
    {
        var table = StagingTable.Rent();
        Secret.AsSpan().CopyTo(table.CharRoom(Secret.Length));
        StagingTable.Return(table);

        var again = StagingTable.Rent();
        var room = again.CharRoom(Secret.Length).ToArray();
        StagingTable.Return(again);

        Assert.IsFalse(room.Any(c => c != '\0'));
    }
}
