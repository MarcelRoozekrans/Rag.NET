using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Renders the prompt <c>ChatAnswerEngine</c> assembled and files it against the current trace.
/// </summary>
/// <remarks>
/// <para>
/// The rendering happens here rather than in the engine on purpose: flattening a message list into a
/// string is a decision about how a prompt should be read, and the engine has no business making it
/// on behalf of a package it knows nothing about.
/// </para>
/// <para>
/// The engine calls this on both the streamed and the non-streamed path — they share
/// <c>BuildMessagesAsync</c> — so a streamed execution can trace what the model was asked even though
/// <see cref="DiagnosticsAnswerEngineDecorator"/> deliberately does not trace what it replied.
/// </para>
/// <para>
/// <b>On the streamed path that depends on the host supplying an ambient activity.</b>
/// <c>ChatAnswerEngine.AskStreamingAsync</c> assembles the prompt <i>after</i> its first
/// <c>yield return</c>, so this runs on the consumer's execution context when the consumer calls
/// <c>MoveNextAsync</c> — and the spans the pipeline started inside its own iterators are not ambient
/// there, whatever they are for the rest of the pipeline. Under ASP.NET the request activity is the
/// consumer's own and carries the trace id everything else joins on, so the prompt joins with it; in a
/// console app or a test with no ambient activity, a streamed prompt is not captured. The chunks, the
/// stage latencies and the commit are unaffected — they are recorded on the pipeline's own context.
/// The non-streamed path has no suspension between the span and the prompt and always captures.
/// </para>
/// <para>
/// The prompt contains the question and the retrieved chunks together, so it is gated on
/// <see cref="RagTraceOptions.CapturePromptText"/>, which retains what
/// <see cref="RagTraceOptions.CaptureQueryText"/> and <see cref="RagTraceOptions.CaptureChunkText"/>
/// do combined.
/// </para>
/// </remarks>
internal sealed partial class TracePromptObserver : IPromptObserver
{
    /// <summary>Separates one rendered message from the next.</summary>
    private const string MessageSeparator = "\n\n";

    private readonly ITraceCollector _collector;
    private readonly ILogger<TracePromptObserver> _logger;

    /// <summary>Creates an observer that records prompts into <paramref name="collector"/>.</summary>
    /// <param name="collector">Where the prompt is recorded.</param>
    /// <param name="logger">Where capture failures go. Optional.</param>
    public TracePromptObserver(ITraceCollector collector, ILogger<TracePromptObserver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(collector);

        _collector = collector;
        _logger = logger ?? NullLogger<TracePromptObserver>.Instance;
    }

    /// <inheritdoc/>
    public void OnPromptAssembled(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            var traceId = TraceCorrelation.CurrentTraceId();

            if (traceId is null || messages is null)
                return;

            _collector.RecordPrompt(traceId, Render(messages));
        }
        catch (Exception ex)
        {
            // IPromptObserver's contract is that implementations never throw: this runs on the path
            // to the model, so anything escaping here would turn a diagnostic into a failed answer.
            LogCaptureFailed(_logger, ex);
        }
    }

    /// <summary>Flattens the messages into something a person can read in a trace.</summary>
    /// <param name="messages">The assembled prompt.</param>
    /// <returns>Each message as <c>role: text</c>, in order.</returns>
    /// <remarks>
    /// The role is kept. Whether a prompt-hardening prefix landed ahead of the system prompt, or the
    /// conversation history arrived at all, is exactly the sort of thing someone opens a trace to
    /// check, and a bare concatenation of the text would hide it.
    /// </remarks>
    private static string Render(IReadOnlyList<ChatMessage> messages)
    {
        var rendered = new string[messages.Count];

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            rendered[i] = message is null ? string.Empty : $"{message.Role}: {message.Text}";
        }

        return string.Join(MessageSeparator, rendered);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to record the assembled prompt into the trace. " +
                  "Answer generation is unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex);
}
