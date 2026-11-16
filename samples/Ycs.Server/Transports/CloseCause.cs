namespace Ycs.Server.Transports;

/// <summary>
/// The exception that closed a session, for the log: its type and message, without its stack. Formatted only when
/// the log line is written.
/// </summary>
internal readonly record struct CloseCause(Exception? Exception)
{
    public static implicit operator CloseCause(Exception? exception) => new(exception);

    public override string ToString() => Exception is null ? "none" : $"{Exception.GetType().Name}: {Exception.Message}";
}
