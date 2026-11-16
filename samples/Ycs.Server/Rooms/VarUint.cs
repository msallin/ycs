namespace Ycs.Server.Rooms;

/// <summary>lib0's variable-length unsigned integer, as y-protocols frames its messages.</summary>
internal static class VarUint
{
    public static void Write(Stream stream, uint value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    /// <summary>The first variable-length integer of <paramref name="message"/>, without consuming it.</summary>
    public static uint Peek(ReadOnlySpan<byte> message)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 35; shift += 7)
        {
            if (shift / 7 >= message.Length)
            {
                throw new YUpdateException("The message ended early.");
            }

            var b = message[shift / 7];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value <= uint.MaxValue ? (uint)value : throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        throw new YUpdateException("A variable-length integer is out of range.");
    }

    public static uint Read(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 35; shift += 7)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new YUpdateException("The message ended early.");
            }

            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value <= uint.MaxValue ? (uint)value : throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        throw new YUpdateException("A variable-length integer is out of range.");
    }
}
