#nullable enable

using System.Diagnostics;

namespace Ycs;

/// <summary>The identity of an item: the client that created it and that client's clock, a continuous number per client.</summary>
/// <param name="Client">The client id, never negative.</param>
/// <param name="Clock">The position in the client's sequence, never negative.</param>
public readonly record struct ItemId(long Client, long Clock)
{
    internal void Write(Lib0Writer writer)
    {
        writer.WriteVarUint((ulong)Client);
        writer.WriteVarUint((ulong)Clock);
    }

    internal static ItemId Read(Lib0Reader reader)
    {
        var client = reader.ReadVarUint53();
        var clock = reader.ReadVarUint();
        Debug.Assert(client >= 0 && clock >= 0);
        return new ItemId(client, clock);
    }
}
