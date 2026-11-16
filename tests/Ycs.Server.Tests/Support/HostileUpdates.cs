namespace Ycs.Server.Tests.Support;

/// <summary>Valid v1 updates a hostile client can make: through a document of its own, or written by hand.</summary>
internal static class HostileUpdates
{
    /// <summary>The update of one transaction of <paramref name="doc"/>: what its client would send.</summary>
    public static byte[] Edit(YDoc doc, Action<YDoc> edit)
    {
        byte[]? update = null;
        void Capture(object? sender, UpdateEventArgs e) => update = e.Update.ToArray();
        doc.Updated += Capture;
        try
        {
            doc.Transact(_ => edit(doc));
        }
        finally
        {
            doc.Updated -= Capture;
        }

        return update ?? throw new InvalidOperationException("The edit changed nothing.");
    }

    /// <summary>An update of no structs that deletes one range of one client.</summary>
    public static byte[] Delete(long client, long clock, long length)
    {
        var stream = new MemoryStream();
        WriteVarUint(stream, 0);
        WriteVarUint(stream, 1);
        WriteVarUint(stream, (ulong)client);
        WriteVarUint(stream, 1);
        WriteVarUint(stream, (ulong)clock);
        WriteVarUint(stream, (ulong)length);
        return stream.ToArray();
    }

    public static void WriteVarUint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
