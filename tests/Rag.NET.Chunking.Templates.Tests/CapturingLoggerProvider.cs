using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Rag.NET.Chunking.Templates.Tests;

/// <summary>
/// Captures every log entry written through the container, so a test can assert on <i>why</i> an
/// attachment was skipped rather than only that it produced no sections.
/// </summary>
/// <remarks>
/// The distinction matters for the <c>application/octet-stream</c> regression. Since Phase 3.11
/// contained a failing attachment parser to its own attachment, a parser that wrongly claims a type
/// and then throws produces the same visible result as no parser claiming it at all — the body and
/// the siblings survive either way. The two are only distinguishable in the log: one is
/// <c>No parser registered for attachment content type ...</c> and the other is
/// <c>Parser ... failed on attachment ...</c>.
/// </remarks>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> messages = new();

    public IReadOnlyCollection<string> Messages => messages;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

    public void Dispose()
    {
        // Nothing to release; the queue outlives the provider so assertions can run after disposal.
    }

    public bool Contains(string fragment)
    {
        foreach (var message in messages)
        {
            if (message.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
