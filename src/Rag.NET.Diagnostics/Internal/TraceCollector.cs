using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// The one place a trace is assembled, and the one place content capture is decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>The content gate lives here and nowhere else.</b> Every text field on its way into a trace goes
/// through <see cref="Capture"/>, which returns <see langword="null"/> when the matching flag is off
/// and a visibly-truncated prefix when the value is longer than
/// <see cref="RagTraceOptions.MaxCapturedCharacters"/>. Four independently-written
/// <c>if (options.CaptureX)</c> checks would be four chances to get it wrong, and the one that would
/// go wrong is the one nobody thought to test.
/// </para>
/// <para>
/// Nothing on the recording path throws. The collector is wired into a live pipeline by decorators
/// and an activity listener, so a defect here would surface as a failed query — a debugger that
/// breaks the thing it observes is worse than no debugger. Failures are swallowed and logged, the
/// posture <c>AuditRetrievalBehavior</c> takes with a failed audit write.
/// </para>
/// </remarks>
internal sealed partial class TraceCollector : ITraceCollector
{
    /// <summary>
    /// How many uncommitted traces may exist at once, as a multiple of the buffer's capacity.
    /// </summary>
    /// <remarks>
    /// A trace that is started and never committed — a request that threw before reaching whatever
    /// calls <see cref="Commit"/> — would otherwise sit in the map forever, which is the memory leak
    /// the ring buffer exists to prevent, reintroduced one level up. Past the ceiling, new traces are
    /// simply not started; traces already in flight still finish. A multiple of the capacity rather
    /// than a constant because both scale with how much concurrency the process is under.
    /// </remarks>
    private const int InFlightPerCapacity = 4;

    private readonly ConcurrentDictionary<string, TraceBuilder> _inFlight = new(StringComparer.Ordinal);
    private readonly RagTraceOptions _options;
    private readonly TraceRingBuffer _buffer;
    private readonly ILogger<TraceCollector> _logger;
    private readonly int _maxInFlight;

    /// <summary>Creates a collector that commits into <paramref name="buffer"/>.</summary>
    /// <param name="options">What to capture, and how much of it.</param>
    /// <param name="buffer">Where committed traces go.</param>
    /// <param name="logger">Where capture failures go. Optional; failures are otherwise silent.</param>
    public TraceCollector(
        RagTraceOptions options,
        TraceRingBuffer buffer,
        ILogger<TraceCollector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(buffer);

        _options = options;
        _buffer = buffer;
        _logger = logger ?? NullLogger<TraceCollector>.Instance;
        _maxInFlight = options.Capacity * InFlightPerCapacity;
    }

    /// <inheritdoc/>
    public void RecordQuery(string traceId, string query) => Update(
        traceId,
        builder =>
        {
            // First write wins, unlike RecordChunks below. A fan-out retriever such as
            // DeepResearchRetriever, or FlareAnswerEngine's lookahead, records once per generated
            // sub-query — and every one of those arrives after the user's. Last-wins would put a
            // sub-query the user never typed in the trace's headline field, and make two identical
            // questions hash differently, which breaks the one field the list route identifies a
            // trace by. "Which chunks came back" is genuinely every retrieval; "what was asked" is
            // genuinely the first.
            // Empty rather than null is "not yet recorded" — TraceBuilder.QueryHash is a
            // non-nullable string defaulting to string.Empty.
            if (!string.IsNullOrEmpty(builder.QueryHash))
                return;

            // The hash is written whether or not the text is: it tells repeated questions apart
            // without retaining what anybody asked, which is the point of the default.
            builder.QueryHash = HashOf(query ?? string.Empty);
            builder.Query = Capture(query, TraceContentKind.Query);
        });

    /// <inheritdoc/>
    public void RecordChunks(string traceId, IReadOnlyList<TraceChunk> chunks) => Update(
        traceId,
        builder =>
        {
            if (chunks is null)
                return;

            // Appended rather than replaced. A pipeline can retrieve more than once — sub-queries,
            // a re-rank pass — and "which chunks came back" should not mean "the last batch".
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                if (chunk is not null)
                    builder.Chunks.Add(chunk with { Text = Capture(chunk.Text, TraceContentKind.Chunk) });
            }
        });

    /// <inheritdoc/>
    public void RecordGuardAction(string traceId, TraceGuardAction action, TraceContentKind contentKind) => Update(
        traceId,
        builder =>
        {
            if (action is null)
                return;

            builder.GuardActions.Add(action with
            {
                InputText = Capture(action.InputText, contentKind),
                OutputText = Capture(action.OutputText, contentKind),
            });
        });

    /// <inheritdoc/>
    public void RecordStage(string traceId, TraceStage stage) => Update(
        traceId,
        builder =>
        {
            if (stage is not null)
                builder.Stages.Add(stage);
        });

    /// <inheritdoc/>
    public void RecordPrompt(string traceId, string prompt) => Update(
        traceId,
        builder => builder.Prompt = Capture(prompt, TraceContentKind.Prompt));

    /// <inheritdoc/>
    public void RecordAnswer(string traceId, string answer) => Update(
        traceId,
        builder => builder.Answer = Capture(answer, TraceContentKind.Answer));

    /// <inheritdoc/>
    public RagTrace? Current(string traceId)
    {
        try
        {
            if (string.IsNullOrEmpty(traceId) || !_inFlight.TryGetValue(traceId, out var builder))
                return null;

            lock (builder.Gate)
                return builder.Build(traceId);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, ex);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Commit(string traceId)
    {
        try
        {
            if (string.IsNullOrEmpty(traceId) || !_inFlight.TryRemove(traceId, out var builder))
                return;

            RagTrace trace;

            lock (builder.Gate)
                trace = builder.Build(traceId);

            _buffer.Add(trace);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, ex);
        }
    }

    /// <summary>The content gate. Every captured text field passes through exactly this method.</summary>
    /// <param name="text">The value as the pipeline had it: unredacted, untruncated.</param>
    /// <param name="kind">What sort of content this is, which decides the flag that governs it.</param>
    /// <returns>
    /// <see langword="null"/> when the flag is off, so "the flag was off" is distinguishable from
    /// "there was no such value"; otherwise the value, cut to
    /// <see cref="RagTraceOptions.MaxCapturedCharacters"/> with
    /// <see cref="RagTraceOptions.TruncationMarker"/> appended when it did not fit.
    /// </returns>
    private string? Capture(string? text, TraceContentKind kind)
    {
        if (text is null || !IsEnabled(kind))
            return null;

        var max = _options.MaxCapturedCharacters;

        return text.Length <= max
            ? text
            : string.Concat(text.AsSpan(0, max), RagTraceOptions.TruncationMarker);
    }

    /// <summary>Maps a kind of content to the one flag that governs it.</summary>
    /// <param name="kind">The kind of content being captured.</param>
    /// <returns>Whether that content is being kept.</returns>
    /// <remarks>
    /// The whole field-to-flag mapping, in one expression, so there is one place to read and one
    /// place to get wrong. An unrecognised kind captures nothing: a future kind added without a flag
    /// to go with it must fail closed, because the failure that matters here is retaining text
    /// nobody asked to retain.
    /// </remarks>
    private bool IsEnabled(TraceContentKind kind) => kind switch
    {
        TraceContentKind.Query => _options.CaptureQueryText,
        TraceContentKind.Chunk => _options.CaptureChunkText,
        TraceContentKind.Prompt => _options.CapturePromptText,
        TraceContentKind.Answer => _options.CaptureAnswerText,
        _ => false,
    };

    /// <summary>A stable, content-free identifier for a query.</summary>
    /// <param name="query">The query text.</param>
    /// <returns>Its SHA-256, lowercase hex.</returns>
    /// <remarks>
    /// SHA-256 rather than <see cref="string.GetHashCode()"/> because the hash has to mean the same
    /// thing across processes and restarts for two traces of the same question to line up, and
    /// string hashing is randomised per process.
    /// </remarks>
    private static string HashOf(string query) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)));

    /// <summary>Applies <paramref name="mutate"/> to the trace under <paramref name="traceId"/>.</summary>
    /// <param name="traceId">The trace to add to, started if it does not exist yet.</param>
    /// <param name="mutate">What to record. Runs under the trace's own lock.</param>
    /// <remarks>
    /// The single try/catch every recording path funnels through. There is no cancellation token on
    /// this surface, so there is no <see cref="OperationCanceledException"/> that needs re-throwing
    /// the way the audit behavior re-throws one: anything caught here is a capture defect, and a
    /// capture defect must not become a failed query.
    /// </remarks>
    private void Update(string traceId, Action<TraceBuilder> mutate)
    {
        try
        {
            if (string.IsNullOrEmpty(traceId))
                return;

            var builder = GetOrStart(traceId);

            if (builder is null)
                return;

            lock (builder.Gate)
                mutate(builder);
        }
        catch (Exception ex)
        {
            LogCaptureFailed(_logger, ex);
        }
    }

    /// <summary>Finds the in-flight trace for an id, starting one if the ceiling allows.</summary>
    /// <param name="traceId">The id to look for.</param>
    /// <returns><see langword="null"/> when too many traces are already in flight.</returns>
    private TraceBuilder? GetOrStart(string traceId)
    {
        if (_inFlight.TryGetValue(traceId, out var existing))
            return existing;

        if (_inFlight.Count >= _maxInFlight)
        {
            LogInFlightCeilingReached(_logger, _maxInFlight);
            return null;
        }

        return _inFlight.GetOrAdd(traceId, static _ => new TraceBuilder(DateTimeOffset.UtcNow));
    }

    [LoggerMessage(
        EventId = 1912556956, EventName = "log_capture_failed",
        Level = LogLevel.Warning,
        Message = "Trace capture failed and the trace was left incomplete. The pipeline is unaffected.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 857107281, EventName = "log_in_flight_ceiling_reached",
        Level = LogLevel.Debug,
        Message = "Not starting a new trace: {MaxInFlight} are already uncommitted. Traces are " +
                  "being started and never committed, which usually means nothing is calling Commit.")]
    private static partial void LogInFlightCeilingReached(ILogger logger, int maxInFlight);

    /// <summary>A trace part-way through being assembled.</summary>
    /// <remarks>
    /// Mutable and lock-guarded, unlike the immutable <see cref="RagTrace"/> it becomes. The parts
    /// arrive from different threads — a stage span stops on whichever thread ran the stage, the
    /// answer decorator continues on another — so the assembly is shared state even though the
    /// finished trace is not.
    /// </remarks>
    private sealed class TraceBuilder(DateTimeOffset startedAt)
    {
        public Lock Gate { get; } = new();

        public string QueryHash { get; set; } = string.Empty;

        public string? Query { get; set; }

        public List<TraceChunk> Chunks { get; } = [];

        public List<TraceGuardAction> GuardActions { get; } = [];

        public List<TraceStage> Stages { get; } = [];

        public string? Prompt { get; set; }

        public string? Answer { get; set; }

        /// <summary>Freezes the trace so far. Call under <see cref="Gate"/>.</summary>
        /// <param name="traceId">The id this trace was assembled under.</param>
        /// <returns>An immutable copy that later recording cannot alter.</returns>
        public RagTrace Build(string traceId) => new()
        {
            TraceId = traceId,
            StartedAt = startedAt,
            QueryHash = QueryHash,
            Query = Query,
            Chunks = [.. Chunks],
            GuardActions = [.. GuardActions],
            Stages = [.. Stages],
            Prompt = Prompt,
            Answer = Answer,
        };
    }
}
