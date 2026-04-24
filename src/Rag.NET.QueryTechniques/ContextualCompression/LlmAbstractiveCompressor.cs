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

        return new List<ChatMessage>
        {
            new(ChatRole.System,
                "You compress retrieved content for a question-answering system. " +
                "Output only the content verbatim-style rewritten to retain information relevant to " +
                "the user's query. Do not include meta-commentary or markdown." + budget),
            new(ChatRole.User, $"Query: {query}\n\nContent:\n{content}"),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Abstractive compression failed for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogCompressionFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Abstractive compression returned empty response for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogEmptyResponse(ILogger logger, string documentId);
}
