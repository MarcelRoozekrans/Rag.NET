using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Abstractive contextual compressor. Issues one <see cref="IChatClient"/> call per chunk
/// in parallel, asking the model to rewrite the chunk keeping only query-relevant content.
/// </summary>
public sealed partial class LlmAbstractiveCompressor(
    IChatClient chatClient,
    ContextualCompressionOptions options,
    ILogger<LlmAbstractiveCompressor>? logger = null) : IContextualCompressor
{
    private readonly IChatClient _chatClient = chatClient;
    private readonly ContextualCompressionOptions _options = options;
    private readonly ILogger<LlmAbstractiveCompressor> _logger = logger ?? NullLogger<LlmAbstractiveCompressor>.Instance;

    public async ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0) return sources;

        cancellationToken.ThrowIfCancellationRequested();

        var tasks = new Task<SearchResult>[sources.Count];
        for (var i = 0; i < sources.Count; i++)
            tasks[i] = CompressOneAsync(sources[i], query, cancellationToken);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<SearchResult> CompressOneAsync(
        SearchResult source,
        string query,
        CancellationToken cancellationToken)
    {
        // Skip already-compressed chunks — prevents double work when compression runs
        // both in the retrieval pipeline and at the answer engine.
        if (source.CompressedText is not null) return source;

        try
        {
            var messages = BuildMessages(source.Chunk.Text, query);
            var response = await _chatClient
                .GetResponseAsync(messages, options: null, cancellationToken)
                .ConfigureAwait(false);

            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                LogEmptyResponse(_logger, source.Chunk.DocumentId.Value);
                return source;
            }
            return source with { CompressedText = text.Trim() };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogCompressionFailed(_logger, source.Chunk.DocumentId.Value, ex);
            return source;
        }
    }

    private List<ChatMessage> BuildMessages(string content, string query)
    {
        // Precedence mirrors ExtractiveCompressor.SelectCompressed: KeepTopSentences wins
        // if set; otherwise fall back to MaxTokensPerChunk; otherwise no budget hint.
        var budget = _options.KeepTopSentences is { } kn
            ? $" Keep at most {kn} sentences."
            : _options.MaxTokensPerChunk is { } mt
                ? $" Target: at most {mt} tokens."
                : string.Empty;

        // Per-call randomized delimiter suffix — defends against prompt-injection attempts
        // that might embed literal </content> or </query> closing tags to escape the fence.
        // Must be a v4 GUID: v7's leading hex chars are timestamp-derived and predictable.
        var delim = Guid.NewGuid().ToString("N")[..8];

        return new List<ChatMessage>
        {
            new(ChatRole.System,
                "You compress retrieved content for a question-answering system. " +
                "Return only the sentences or clauses directly relevant to the query. " +
                "Preserve the original wording where possible; do not paraphrase beyond what is needed " +
                "to drop irrelevant content. Output no markdown, no preamble, no meta-commentary — " +
                "just the compressed content." + budget),
            new(ChatRole.User, $"<query-{delim}>{query}</query-{delim}>\n\n<content-{delim}>\n{content}\n</content-{delim}>"),
        };
    }

    [LoggerMessage(EventId = 501829536, EventName = "log_compression_failed", Level = LogLevel.Warning,
        Message = "Abstractive compression failed for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogCompressionFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(EventId = 1052473395, EventName = "log_empty_response", Level = LogLevel.Warning,
        Message = "Abstractive compression returned empty response for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogEmptyResponse(ILogger logger, string documentId);
}
