using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Runtime.CompilerServices;

namespace Rag.NET.Chunking;

/// <summary>
/// Late chunking: the whole section text is embedded in a single pass by an
/// <see cref="ITokenEmbeddingGenerator"/> (so every token vector carries whole-document
/// context), then overlapping token windows are cut and each chunk embedding is the
/// L2-normalized mean of its window's token vectors. On generator failure, chunks are still
/// produced from cl100k token windows over the same window/overlap sizes — without embeddings.
/// </summary>
public sealed partial class LateChunkingStrategy(
    ITokenEmbeddingGenerator generator,
    LateChunkingOptions options,
    ILogger<LateChunkingStrategy>? logger = null) : IDocumentChunkingStrategy, IChunkingStrategy
{
    private readonly LateChunkingOptions _options = ValidateOptions(options);
    private readonly ILogger<LateChunkingStrategy> _logger = logger ?? NullLogger<LateChunkingStrategy>.Instance;

    // chunkingOptions (MaxChunkSize/Overlap) is not used — window sizing is governed by
    // LateChunkingOptions.WindowSizeTokens/OverlapTokens. The parameter is required by the interface.
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var chunkIndex = 0;
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(section.Text))
                continue;

            // yield return cannot appear inside try-with-catch, so each section's chunks are
            // collected first; generator failure falls back to unembedded token windows.
            List<TextChunk> chunks;
            try
            {
                chunks = await ChunkSectionAsync(section, chunkIndex, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TokenEmbeddingContractException ex)
            {
                // A broken matrix is a generator BUG, not a transient failure — log at Error.
                LogTokenEmbeddingContractViolation(_logger, section.DocumentId.Value, ex);
                chunks = FallbackChunks(section, chunkIndex);
            }
            catch (Exception ex)
            {
                LogTokenEmbeddingFailure(_logger, section.DocumentId.Value, ex);
                chunks = FallbackChunks(section, chunkIndex);
            }

            for (var i = 0; i < chunks.Count; i++)
                yield return chunks[i];
            chunkIndex += chunks.Count;
        }
    }

    /// <summary>
    /// Per-section entry point (<see cref="IChunkingStrategy"/>): wraps the single section in a
    /// one-element async sequence and delegates to <see cref="ChunkDocumentAsync"/>.
    /// </summary>
    public IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        CancellationToken cancellationToken = default) =>
        ChunkDocumentAsync(SingleAsync(section), chunkingOptions, cancellationToken);

    /// <summary>
    /// Shared by the constructor and <c>UseLateChunking</c> so misconfiguration fails at
    /// registration time with the same rules.
    /// </summary>
    internal static LateChunkingOptions ValidateOptions(LateChunkingOptions options)
    {
        if (options.WindowSizeTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"WindowSizeTokens ({options.WindowSizeTokens}) must be greater than zero.");
        }

        if (options.OverlapTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"OverlapTokens ({options.OverlapTokens}) must not be negative.");
        }

        if (options.OverlapTokens >= options.WindowSizeTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"OverlapTokens ({options.OverlapTokens}) must be smaller than WindowSizeTokens ({options.WindowSizeTokens}).");
        }

        return options;
    }

    private static async IAsyncEnumerable<DocumentSection> SingleAsync(DocumentSection section)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return section;
    }

    private async Task<List<TextChunk>> ChunkSectionAsync(
        DocumentSection section,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var activeGenerator = _options.Generator ?? generator;
        var result = await activeGenerator.GenerateAsync(section.Text, cancellationToken).ConfigureAwait(false);
        ValidateResult(result);

        var windows = TokenWindowSplitter.Split(
            result.TokenOffsets, section.Text.Length, _options.WindowSizeTokens, _options.OverlapTokens);

        var chunks = new List<TextChunk>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!TryTrimSpan(section.Text, window.Start, window.End, out var start, out var end))
                continue;

            var embedding = PoolWindow(result, window.FirstToken, window.LastToken);
            chunks.Add(MakeChunk(section, startIndex + chunks.Count, start, end, embedding));
        }

        return chunks;
    }

    /// <summary>
    /// Trims whitespace off both ends of the span <c>[windowStart..windowEnd)</c> and returns
    /// the trimmed char positions, so <c>Text == section.Text[StartPosition..EndPosition]</c>
    /// holds even when token spans include leading/trailing whitespace. Returns
    /// <see langword="false"/> when the span is all whitespace.
    /// </summary>
    private static bool TryTrimSpan(string text, int windowStart, int windowEnd, out int start, out int end)
    {
        start = windowStart;
        while (start < windowEnd && char.IsWhiteSpace(text[start]))
            start++;

        end = windowEnd;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        return end > start;
    }

    /// <summary>
    /// Guards the row-major matrix contract before any row slicing; a mismatch (or a
    /// non-positive dimension, which could only ever yield empty embeddings — treated as
    /// absent by EmbeddingBehavior) is a generator failure handled by the fallback catch.
    /// </summary>
    private static void ValidateResult(TokenEmbeddingResult result)
    {
        if (result.Dimension <= 0)
            throw new TokenEmbeddingContractException($"Token embedding dimension ({result.Dimension}) must be greater than zero.");

        var expected = result.TokenOffsets.Count * result.Dimension;
        if (result.Embeddings.Length != expected)
        {
            throw new TokenEmbeddingContractException(
                $"Token embedding matrix length ({result.Embeddings.Length}) does not match " +
                $"TokenOffsets.Count x Dimension ({result.TokenOffsets.Count} x {result.Dimension} = {expected}).");
        }
    }

    /// <summary>
    /// Marks a <see cref="TokenEmbeddingResult"/> that violates the matrix contract, so the
    /// fallback path can log it at Error (generator bug) instead of Warning (transient failure).
    /// </summary>
    private sealed class TokenEmbeddingContractException : InvalidOperationException
    {
        public TokenEmbeddingContractException() { }
        public TokenEmbeddingContractException(string message) : base(message) { }
        public TokenEmbeddingContractException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Mean-pools rows <paramref name="firstToken"/>..<paramref name="lastToken"/> (inclusive)
    /// of the row-major token matrix, then L2-normalizes. Returns <see langword="null"/> for a
    /// zero-norm mean — a null embedding (embed-normally) beats a meaningless zero vector.
    /// </summary>
    private static float[]? PoolWindow(TokenEmbeddingResult result, int firstToken, int lastToken)
    {
        var dimension = result.Dimension;
        var pooled = new float[dimension];
        var matrix = result.Embeddings.Span;
        for (var token = firstToken; token <= lastToken; token++)
        {
            var row = matrix.Slice(token * dimension, dimension);
            for (var d = 0; d < dimension; d++)
                pooled[d] += row[d];
        }

        var count = lastToken - firstToken + 1;
        double normSquared = 0;
        foreach (ref var value in pooled.AsSpan())
        {
            value /= count;
            normSquared += (double)value * value;
        }

        if (normSquared == 0)
            return null;

        var norm = (float)Math.Sqrt(normSquared);
        foreach (ref var value in pooled.AsSpan())
            value /= norm;

        return pooled;
    }

    /// <summary>
    /// Generator-failure fallback: same window/overlap sizes over cl100k tokens of the section
    /// text, producing text-only chunks. Embedding stays <see langword="null"/> so the
    /// pipeline embeds them normally downstream.
    /// </summary>
    private List<TextChunk> FallbackChunks(DocumentSection section, int startIndex)
    {
        var windows = TokenWindowSplitter.SplitByCl100kTokens(
            section.Text, _options.WindowSizeTokens, _options.OverlapTokens);

        var chunks = new List<TextChunk>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!TryTrimSpan(section.Text, window.Start, window.End, out var start, out var end))
                continue;

            chunks.Add(MakeChunk(section, startIndex + chunks.Count, start, end, embedding: null));
        }

        return chunks;
    }

    private static TextChunk MakeChunk(
        DocumentSection section,
        int index,
        int start,
        int end,
        float[]? embedding) =>
        new()
        {
            Text = section.Text[start..end],
            DocumentId = section.DocumentId,
            ChunkIndex = index,
            StartPosition = start,
            EndPosition = end,
            Metadata = PageMetadata.ForPage(section.PageNumber),
            // Part B contract: null when absent — the cast is load-bearing: without it the
            // null branch converts via the implicit float[] → ReadOnlyMemory<float> operator
            // into an empty, NON-null memory (exactly the trap TextChunk.Embedding documents).
            Embedding = embedding is null ? (ReadOnlyMemory<float>?)null : new ReadOnlyMemory<float>(embedding),
        };

    [LoggerMessage(EventId = 1371604333, EventName = "log_token_embedding_failure", Level = LogLevel.Warning,
        Message = "Token embedding generation failed for document {DocumentId}; falling back to token-window chunks without embeddings.")]
    private static partial void LogTokenEmbeddingFailure(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(EventId = 1235670448, EventName = "log_token_embedding_contract_violation", Level = LogLevel.Error,
        Message = "Token embedding generator returned a result violating the matrix contract for document {DocumentId}; falling back to token-window chunks without embeddings.")]
    private static partial void LogTokenEmbeddingContractViolation(ILogger logger, string documentId, Exception ex);
}
