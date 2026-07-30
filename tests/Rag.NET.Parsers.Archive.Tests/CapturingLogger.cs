using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>Captures log entries so tests can assert warning behaviour.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IEnumerable<string> Warnings =>
        Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
