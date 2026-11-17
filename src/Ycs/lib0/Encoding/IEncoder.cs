namespace Ycs;

internal interface IEncoder<T>
{
    void Write(T value);

    /// <summary>
    /// Flushes the pending run (the first time) and returns the encoded bytes.
    /// </summary>
    ReadOnlySpan<byte> GetWrittenSpan();
}
