namespace Ycs;

/// <summary>
/// Base class of the lib0 column encoders used by the v2 update format. Each one writes
/// into its own <see cref="Lib0Writer"/>; <see cref="GetWrittenSpan"/> flushes any pending
/// run (once) and returns the bytes; <see cref="Reset"/> makes the encoder as new.
/// </summary>
internal abstract class AbstractStreamEncoder<T> : IEncoder<T>
{
    private bool _flushed;

    /// <param name="pooled">Rent the buffer from the pool; <see cref="Reset"/> returns it.</param>
    protected AbstractStreamEncoder(bool pooled = false)
    {
        Writer = new Lib0Writer(pooled: pooled);
    }

    protected Lib0Writer Writer { get; }

    public void Write(T value)
    {
        if (_flushed)
        {
            // The pending run was written already; a value now would be lost.
            throw new InvalidOperationException("The column was already flushed; reset the encoder first.");
        }

        WriteValue(value);
    }

    /// <summary>
    /// Flushes the pending run (the first time) and returns the encoded bytes. Writing after that
    /// throws until <see cref="Reset"/>.
    /// </summary>
    public ReadOnlySpan<byte> GetWrittenSpan()
    {
        if (!_flushed)
        {
            Flush();
            _flushed = true;
        }

        return Writer.WrittenSpan;
    }

    public byte[] ToArray() => GetWrittenSpan().ToArray();

    /// <summary>Forgets everything written, and returns a pooled buffer.</summary>
    public virtual void Reset()
    {
        Writer.Reset();
        _flushed = false;
    }

    protected abstract void WriteValue(T value);

    protected virtual void Flush()
    {
        // Do nothing.
    }
}
