using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Evaluation.Tests.Shadow;

/// <summary>
/// An <see cref="IRagPipeline"/> whose <see cref="AskAsync"/> the test scripts, counting every
/// call. Every other member is unsupported: the shadow path asks and does nothing else, so a
/// shadow that touched ingestion or deletion would fail loudly here rather than silently succeed.
/// </summary>
/// <remarks>
/// Hand-written rather than NSubstitute because the isolation tests script behaviours a canned
/// return cannot express — a task that never completes, a synchronous throw — and because
/// <see cref="AskCalls"/> must be exact under concurrency: the decorator tests pin "the decorator
/// never ran the secondary" on it.
/// </remarks>
internal sealed class ScriptedRagPipeline(
    Func<string, RagOptions?, CancellationToken, Task<RagResponse>>? ask = null) : IRagPipeline
{
    private int _askCalls;

    /// <summary>How many times <see cref="AskAsync"/> was invoked.</summary>
    public int AskCalls => Volatile.Read(ref _askCalls);

    /// <summary>A response whose sources carry exactly <paramref name="contextTexts"/>.</summary>
    public static RagResponse Response(string answer, params string[] contextTexts)
    {
        var sources = new SearchResult[contextTexts.Length];
        for (var i = 0; i < contextTexts.Length; i++)
        {
            sources[i] = new SearchResult
            {
                Chunk = new TextChunk
                {
                    Text = contextTexts[i],
                    DocumentId = new DocumentId("doc-1"),
                    ChunkIndex = i,
                },
                Score = 0.9,
            };
        }

        return new RagResponse { Answer = answer, Sources = sources };
    }

    public Task<RagResponse> AskAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _askCalls);
        return ask is null
            ? Task.FromResult(Response("scripted-answer", "scripted-context"))
            : ask(query, options, cancellationToken);
    }

    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Only AskAsync is scripted; the shadow path must not ingest.");

    public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Only AskAsync is scripted; the shadow path must not retrieve.");

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Only AskAsync is scripted; the shadow path must not stream.");

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Only AskAsync is scripted; the shadow path must not delete.");
}
