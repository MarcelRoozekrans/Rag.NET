using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Cli.Tests;

/// <summary>
/// A configurable <see cref="IRagPipeline"/> test double. <see cref="IngestResult"/> and
/// <see cref="RetrieveResult"/> supply what each call returns; every call is recorded so a test
/// can assert what a command actually passed through, without a host, configuration, or a
/// launched process.
/// </summary>
internal sealed class FakeRagPipeline : IRagPipeline
{
    public List<(DocumentMetadata Metadata, IngestionOptions? Options)> IngestCalls { get; } = [];

    public List<(string Query, RetrievalOptions? Options)> RetrieveCalls { get; } = [];

    public Func<DocumentMetadata, Result<IngestionResult, RagError>> IngestResult { get; set; } =
        metadata => Result<IngestionResult, RagError>.Success(
            new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 1 });

    public Func<string, Result<IReadOnlyList<SearchResult>, RagError>> RetrieveResult { get; set; } =
        static _ => Result<IReadOnlyList<SearchResult>, RagError>.Success([]);

    public Task<Result<IngestionResult, RagError>> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IngestCalls.Add((metadata, options));
        return Task.FromResult(IngestResult(metadata));
    }

    public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        RetrieveCalls.Add((query, options));
        return Task.FromResult(RetrieveResult(query));
    }

    public Task<RagResponse> AskAsync(
        string query, RagOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No ragnet command calls AskAsync; query uses RetrieveAsync.");

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query, RagOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No ragnet command calls AskStreamingAsync.");

    public Task DeleteAsync(string documentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("No ragnet command calls DeleteAsync.");
}
