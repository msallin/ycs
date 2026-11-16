using Microsoft.Extensions.Logging;

namespace Ycs.Server.Tests.Support;

/// <summary>Keeps every log entry: its level and its structured values.</summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Values)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entries.Add((logLevel, values.ToDictionary(v => v.Key, v => v.Value)));
    }
}
