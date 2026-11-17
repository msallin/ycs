namespace Ycs;

/// <summary>N-th bit activated; the bits the codecs use.</summary>
internal static class Bit
{
    public const uint Bit1 = 1 << 0;
    public const uint Bit6 = 1 << 5;
    public const uint Bit7 = 1 << 6;
    public const uint Bit8 = 1 << 7;
}

/// <summary>First N bits activated.</summary>
internal static class Bits
{
    public const uint Bits5 = (1 << 5) - 1;
    public const uint Bits32 = 0xFFFFFFFF;
}
