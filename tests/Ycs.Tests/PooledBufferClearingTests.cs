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

    private static byte[] SecretUpdate(UpdateFormat format = UpdateFormat.V1)
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, Secret);
        return doc.EncodeStateAsUpdate(format: format);
    }

    [TestMethod]
    public void BufferPoolReturn_WithAUsedCount_ClearsThatPrefix()
    {
        var array = BufferPool<byte>.Rent(64);
        array.AsSpan(0, 40).Fill(0x5A);

        BufferPool<byte>.Return(array, 40);

        Assert.IsFalse(array.Contains((byte)0x5A));
    }

    [TestMethod]
    public void ApplyUpdate_FromASpan_LeavesNoContentInTheRentedCopy()
    {
        var update = SecretUpdate();
        new YDoc().ApplyUpdate(update.AsSpan());

        var rented = ArrayPool<byte>.Shared.Rent(update.Length);
        var holds = Holds(rented);
        ArrayPool<byte>.Shared.Return(rented);

        Assert.IsFalse(holds);
    }

    [TestMethod]
    public void Decode_FromASpan_LeavesNoContentInTheRentedCopy()
    {
        var update = SecretUpdate();
        YUpdate.Decode(update.AsSpan());

        var rented = ArrayPool<byte>.Shared.Rent(update.Length);
        var holds = Holds(rented);
        ArrayPool<byte>.Shared.Return(rented);

        Assert.IsFalse(holds);
    }

    [TestMethod]
    public void PooledCopy_Disposed_LeavesNoContent()
    {
        var update = SecretUpdate();
        var copy = new PooledCopy(update);
        var array = copy.Segment.Array!;

        copy.Dispose();

        Assert.IsFalse(Holds(array));
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
