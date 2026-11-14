using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Telemetry;
using ZeroAlloc.Results;

namespace Rag.NET.Pipeline;

/// <summary>
/// Thin coordinator that delegates to <see cref="IRetriever"/>, <see cref="IIngestor"/>,
/// and <see cref="IAnswerEngine"/>. The public <see cref="IRagPipeline"/> facade is unchanged.
/// </summary>
public sealed class RagPipeline(
    IRetriever retriever,
    IIngestor ingestor,
    IAnswerEngine? answerEngine = null,
    ILogger<RagPipeline>? logger = null) : IRagPipeline
{
    /// <summary>
    /// The span opened around one whole query, whichever public entry point started it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ragnet.retrieve</c> and <c>ragnet.ask</c> are opened by <c>PipelineRetriever</c> and
    /// <c>ChatAnswerEngine</c> respectively, one after the other, so without a span here they are
    /// <b>siblings</b>. Two things follow, and both are wrong. When the host supplies no ambient
    /// activity — a console app, a worker, a test — each sibling is created as its own <i>root</i>
    /// with its own trace id, so the two halves of one query cannot be correlated at all; only under
    /// ASP.NET, where the request activity parents both, do they share an id. And there is no moment
    /// at which "the query is over": the last stage span to stop is <c>ragnet.ask</c> in one path and
    /// <c>ragnet.retrieve</c> in the other, so nothing observing spans can tell a finished ask from a
    /// finished retrieval.
    /// </para>
    /// <para>
    /// One span enclosing both fixes both: the children inherit its trace id wherever the pipeline
    /// runs, and its own stop is unambiguously the end of the query. It is pure instrumentation —
    /// no tag is set, nothing is timed twice, and <c>StartActivity</c> returns <see langword="null"/>
    /// unless a listener is subscribed to the <c>Rag.NET</c> source, so a pipeline nobody is
    /// observing does exactly what it did before.
    /// </para>
    /// <para>
    /// <b><c>RetrieveAsync</c> opens it too</b>, which it originally did not. The reasoning for leaving
    /// it out was that a retrieval on its own <i>is</i> the whole operation, so <c>ragnet.retrieve</c>
    /// already marks it and an enclosing span would never have more than one child. That premise is
    /// false whenever a fan-out retriever is registered: <c>DeepResearchRetriever</c> decorates
    /// <see cref="IRetriever"/> globally whenever <c>DeepResearchOptions</c> is configured, and calls
    /// the inner retriever once per sub-question, so a single <c>RetrieveAsync</c> opens
    /// <c>ragnet.retrieve</c> <i>N</i> times. (Multi-query fans out too, but through
    /// <c>MultiQueryBehavior</c> <i>inside</i> the behavior chain, below the one span
    /// <c>PipelineRetriever</c> opens — so it does not multiply spans and was never affected.)
    /// Each of those N was then the outermost pipeline span of its own execution — N root
    /// spans in a console app, N unrelatable siblings under a request — and anything committing on
    /// "the outermost pipeline span stopped" committed N times for one call. Under an ambient request
    /// activity the N fragments even share the request's trace id, so a fetch-by-id keyed on it can
    /// only ever return one of them. One span per public entry point makes "one trace per query" true
    /// rather than true-unless-decorated.
    /// </para>
    /// </remarks>
    private const string QuerySpanName = "ragnet.query";

    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ingestor.IngestAsync(document, metadata, options, progress, cancellationToken);

    public async Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity(QuerySpanName);

        return await retriever.RetrieveAsync(query, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskAsync.");

        using var activity = RagTelemetry.ActivitySource.StartActivity(QuerySpanName);
        using var scope = logger?.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["query_hash"] = PipelineRetriever.HashQuery(query) });

        var opts = options ?? new RagOptions();
        var sources = await RetrieveForAskAsync(query, opts, activity, cancellationToken).ConfigureAwait(false);

        return await answerEngine.AskAsync(query, sources, opts, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (answerEngine is null)
            throw new InvalidOperationException(
                "IAnswerEngine is not registered. Register an IChatClient in DI to use AskStreamingAsync.");

        using var activity = RagTelemetry.ActivitySource.StartActivity(QuerySpanName);
        using var scope = logger?.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["query_hash"] = PipelineRetriever.HashQuery(query) });

        var opts = options ?? new RagOptions();
        var sources = await RetrieveForAskAsync(query, opts, activity, cancellationToken).ConfigureAwait(false);

        await foreach (var update in answerEngine.AskStreamingAsync(query, sources, opts, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => ingestor.DeleteAsync(documentId, cancellationToken);

    // Failures raised here happen before the retriever's or answer engine's own spans could record
    // them, so the query span carries the error.
    private async Task<IReadOnlyList<SearchResult>> RetrieveForAskAsync(
        string query,
        RagOptions options,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        RetrievalOptions retrievalOptions;
        try
        {
            retrievalOptions = BuildRetrievalOptions(options);
        }
        catch (ArgumentException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }

        var retrievalResult = await retriever.RetrieveAsync(query, retrievalOptions, cancellationToken).ConfigureAwait(false);
        if (!retrievalResult.IsSuccess)
        {
            var message = $"Retrieval failed: {retrievalResult.Error}";
            activity?.SetStatus(ActivityStatusCode.Error, message);
            // Carry the cause, so a caller can tell a refusal such as a spent budget from a broken
            // store without parsing the message (#948).
            throw new InvalidOperationException(message, retrievalResult.Error switch
            {
                RagError.StorageFailed e => e.Inner,
                RagError.ModelCallFailed e => e.Inner,
                RagError.TransportFailed e => e.Inner,
                _ => null,
            });
        }

        return retrievalResult.Value;
    }

    private static RetrievalOptions BuildRetrievalOptions(RagOptions options)
    {
        if (options.Retrieval is { } retrieval)
        {
            var assigned = options.AssignedRetrievalFields;
            if (assigned.Count > 0)
                throw new ArgumentException(
                    $"RagOptions.Retrieval was set together with {string.Join(", ", assigned)}. "
                    + "Retrieval replaces those fields; set their values on RagOptions.Retrieval instead.",
                    nameof(options));

            return retrieval;
        }

        return new RetrievalOptions
        {
            TopK = options.TopK,
            MaxContextTokens = options.MaxContextTokens,
            MinScore = options.MinScore,
            MetadataFilter = options.MetadataFilter,
            UseHybridSearch = options.UseHybridSearch,
            UseLostInTheMiddleReordering = options.UseLostInTheMiddleReordering,
            UseRedundancyFilter = options.UseRedundancyFilter,
            RedundancyThreshold = options.RedundancyThreshold,
        };
    }
}
