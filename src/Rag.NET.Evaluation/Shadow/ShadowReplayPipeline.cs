using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// One side of a captured pair, replayed as a pipeline: answers come from storage, and a side the
/// capture recorded as failed throws again, so the offline runner books it under run failures
/// exactly as it would a live one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serves the captures in capture order, by position.</b> Not by question lookup: production
/// asks the same question more than once, and above temperature zero captures different answers
/// each time, so a question is not a key. The offline runner asks each variant every sample in
/// sample order, and <see cref="ShadowReplay"/> builds its sample list in capture order, so
/// position <c>n</c> of the replay answers sample <c>n</c>. The question is still verified on
/// every call — a caller who filtered or reordered the samples gets a failure line naming the
/// mismatch, never a quietly wrong answer scored as a result.
/// </para>
/// <para>
/// The position wraps at the capture count, so one replay can be compared more than once — with
/// different suites, say — without rebuilding it.
/// </para>
/// </remarks>
internal sealed class ShadowReplayPipeline : IRagPipeline
{
    private readonly IReadOnlyList<ShadowCapture> _captures;
    private readonly Func<ShadowCapture, ShadowVariantCapture> _side;
    private int _asked;

    /// <summary>Creates the replay of one side of the captured pairs.</summary>
    /// <param name="captures">The captured pairs, in the order the samples were built in. Never empty.</param>
    /// <param name="side">Which side of each pair this pipeline replays.</param>
    public ShadowReplayPipeline(
        IReadOnlyList<ShadowCapture> captures,
        Func<ShadowCapture, ShadowVariantCapture> side)
    {
        _captures = captures;
        _side = side;
    }

    /// <inheritdoc />
    public Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Interlocked out of caution rather than need: the A/B runner is strictly sequential, but
        // a torn position here would misattribute answers, which is the one lie this type exists
        // to make impossible.
        var position = (Interlocked.Increment(ref _asked) - 1) % _captures.Count;
        var capture = _captures[position];

        if (!string.Equals(capture.Question, query, StringComparison.Ordinal))
        {
            return Task.FromException<RagResponse>(new InvalidOperationException(
                $"Replay expected capture order's '{capture.Question}' at position {position} and was " +
                $"asked '{query}'. A replayed pipeline serves the captures in capture order — run it " +
                "over the Samples list of the ShadowReplay it belongs to, unfiltered and unreordered."));
        }

        var variant = _side(capture);
        if (variant.Failure is { } failure)
        {
            // The capture stores the production failure as "{TypeName}: {Message}", so the line
            // the offline report prints reads "InvalidOperationException: {TypeName}: {Message}" —
            // the replay's framing first, the production fault it carries second.
            return Task.FromException<RagResponse>(new InvalidOperationException(failure.Message));
        }

        return Task.FromResult(new RagResponse { Answer = variant.Answer!, Sources = Sources(variant) });
    }

    /// <summary>The captured context texts, dressed as retrieval results.</summary>
    /// <remarks>
    /// The offline scorer reads <c>Chunk.Text</c> and nothing else; document id, chunk index and
    /// score are replay placeholders, which is why the capture never stored them.
    /// </remarks>
    private static SearchResult[] Sources(ShadowVariantCapture variant)
    {
        var sources = new SearchResult[variant.ContextTexts.Count];
        for (var i = 0; i < sources.Length; i++)
        {
            sources[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = variant.ContextTexts[i],
                    DocumentId = new DocumentId("captured"),
                    ChunkIndex = i,
                },
                Score = 1.0,
            };
        }

        return sources;
    }

    /// <inheritdoc />
    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A replayed capture set can only be asked its captured questions; it has no store to ingest into.");

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A replayed capture set can only be asked its captured questions; retrieval already happened in production.");

    /// <inheritdoc />
    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A replayed capture set can only be asked its captured questions; the captured answer is not a stream.");

    /// <inheritdoc />
    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "A replayed capture set can only be asked its captured questions; there is nothing to delete.");
}
