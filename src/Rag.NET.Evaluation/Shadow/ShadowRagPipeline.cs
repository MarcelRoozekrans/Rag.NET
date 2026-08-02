using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// The shadow-mode decorator: the caller always gets the primary's answer, and each
/// <see cref="AskAsync"/> also hands the background consumer what it needs to run the secondary
/// out-of-band. Everything that is not <see cref="AskAsync"/> is straight delegation.
/// </summary>
/// <remarks>
/// The secondary is made structurally incapable of affecting the primary, in the design's three
/// steps: the primary completes first and only its finished result is scheduled, so the shadow
/// observes a completed primary; scheduling is an enqueue that always completes synchronously and
/// whose whole path is caught, so it can neither block nor throw into a caller who was already
/// served; and the secondary itself runs on <see cref="ShadowCaptureConsumer"/>, where a failure
/// becomes a recorded <see cref="ShadowVariantFailure"/> rather than an exception. Deliberately
/// not a <c>try/catch</c> around an awaited secondary: that catches the exception but still
/// couples the primary's latency to the secondary's, degrading the very requests the shadow is
/// supposed to be invisible to.
/// </remarks>
public sealed class ShadowRagPipeline : IRagPipeline
{
    private readonly IRagPipeline _primary;
    private readonly IRagPipeline _secondary;
    private readonly ShadowCaptureQueue _queue;
    private readonly ShadowPipelineOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Func<double> _sampleSource;

    /// <summary>Wraps the primary pipeline the caller is served by.</summary>
    /// <param name="primary">The pipeline whose responses the caller gets, always.</param>
    /// <param name="secondary">The pipeline shadowed out-of-band; never run on the request path.</param>
    /// <param name="queue">Where pending captures are handed to the background consumer.</param>
    /// <param name="options">The sample rate and variant labels; validated where they are set.</param>
    /// <param name="timeProvider">Clock for latency and the capture timestamp; defaults to the system clock.</param>
    /// <param name="logger">Optional; a scheduling failure — the caller never sees one — is logged here.</param>
    /// <param name="sampleSource">
    /// The randomness behind sampling: a draw in [0, 1), compared against
    /// <see cref="ShadowPipelineOptions.SampleRate"/>. Defaults to <see cref="Random.Shared"/>.
    /// Injected so tests can assert sampling deterministically; rates 0 and 1 never consult it
    /// at all, so those two cases are deterministic even under the default source.
    /// </param>
    /// <exception cref="ArgumentNullException">Any of the required arguments is null.</exception>
    /// <exception cref="ArgumentException">
    /// The two variant names are identical: every capture would be unscoreable, refused here
    /// rather than on the consumer after production traffic already flowed.
    /// </exception>
    public ShadowRagPipeline(
        IRagPipeline primary,
        IRagPipeline secondary,
        ShadowCaptureQueue queue,
        ShadowPipelineOptions options,
        TimeProvider? timeProvider = null,
        ILogger<ShadowRagPipeline>? logger = null,
        Func<double>? sampleSource = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);

        if (string.Equals(options.PrimaryVariantName, options.SecondaryVariantName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Both sides are labelled '{options.PrimaryVariantName}'. The offline scorer keys " +
                "everything by variant name and refuses duplicates, so no capture this decorator " +
                "took could ever be scored.", nameof(options));
        }

        _primary = primary;
        _secondary = secondary;
        _queue = queue;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ShadowRagPipeline>.Instance;
        _sampleSource = sampleSource ?? Random.Shared.NextDouble;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The isolation contract, in order: the primary completes; its finished result is enqueued
    /// for the consumer — synchronously, and inside a catch, so a caller who has been served can
    /// no longer be failed; the response is returned. A primary that throws still throws:
    /// shadowing must not swallow genuine failures, and with no served answer there is nothing
    /// to pair a secondary against. Whether the request is shadowed at all is decided up front
    /// by <see cref="ShouldSample"/>; an unsampled request is a plain pass-through.
    /// </remarks>
    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldSample())
        {
            return await _primary.AskAsync(query, options, cancellationToken).ConfigureAwait(false);
        }

        var started = _timeProvider.GetTimestamp();
        var response = await _primary.AskAsync(query, options, cancellationToken).ConfigureAwait(false);

        await ScheduleShadowAsync(query, options, response, started).ConfigureAwait(false);

        return response;
    }

    /// <summary>Decides whether this request is shadowed, before the primary runs.</summary>
    /// <remarks>
    /// The extremes never consult the source: rate 0 — the default — takes nothing, so a
    /// registered-but-not-enabled shadow costs no draw per request, and rate 1 takes everything,
    /// so a custom source returning exactly 1.0 cannot make "shadow everything" skip requests.
    /// Between them a draw strictly below the rate is sampled — with a half-open [0, 1) source
    /// the probability of sampling is then exactly the rate.
    /// </remarks>
    private bool ShouldSample()
    {
        var rate = _options.SampleRate;
        if (rate <= 0.0)
        {
            return false;
        }

        return rate >= 1.0 || _sampleSource() < rate;
    }

    /// <summary>
    /// Enqueues the pending capture: the completed primary side plus everything the consumer
    /// needs to run the secondary and finish the pair. Never throws — from here on the caller
    /// has an answer, and nothing about the shadow may take it away.
    /// </summary>
    /// <remarks>
    /// Always completes synchronously: the queue's write is non-blocking by contract, and every
    /// awaited task here is already complete. The enqueue deliberately ignores the caller's
    /// cancellation token — a caller that cancels after the primary answered was still served,
    /// and the sample is still a sample.
    /// </remarks>
    private async ValueTask ScheduleShadowAsync(
        string query,
        RagOptions? options,
        RagResponse response,
        long started)
    {
        try
        {
            var pending = new PendingShadowCapture(
                query,
                options,
                new ShadowVariantCapture(
                    _options.PrimaryVariantName,
                    response.Answer,
                    ShadowContextTexts.From(response.Sources),
                    _timeProvider.GetElapsedTime(started)),
                _secondary,
                _options.SecondaryVariantName,
                _timeProvider.GetUtcNow());

            await _queue.EnqueueAsync(pending, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ShadowLog.ShadowSchedulingFailed(_logger, exception);
        }
    }

    /// <inheritdoc/>
    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _primary.IngestAsync(document, metadata, options, progress, cancellationToken);

    /// <inheritdoc/>
    public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => _primary.RetrieveAsync(query, options, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Deliberately not shadowed: a streamed answer completes token by token on the caller's
    /// schedule, so there is no single completed primary result to pair a secondary against
    /// without buffering the caller's stream — which would put shadow work on the request path.
    /// </remarks>
    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
        => _primary.AskStreamingAsync(query, options, cancellationToken);

    /// <inheritdoc/>
    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => _primary.DeleteAsync(documentId, cancellationToken);
}
