using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// Captures log entries with their <see cref="EventId"/>, so a test can assert which event was
/// written rather than only that some text was.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, eventId, formatter(state, exception), exception));
}
