using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>
/// Extracts a hierarchical mind-map tree from document text using an LLM.
/// Optionally persists nodes and edges to an IGraphStore.
/// </summary>
public sealed partial class MindMapExtractor
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IChatClient _chatClient;
    private readonly IGraphStore? _graphStore;
    private readonly MindMapOptions _options;
    private readonly ILogger _logger;

    public MindMapExtractor(
        IChatClient chatClient,
        IGraphStore? graphStore,
        MindMapOptions options,
        ILogger<MindMapExtractor>? logger = null)
    {
        _chatClient = chatClient;
        _graphStore = graphStore;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Extract a mind-map tree from <paramref name="text"/>. If an IGraphStore was provided,
    /// nodes are written as GraphEntity (Type = "mind_map_node") and edges as GraphRelationship
    /// (Description = "has_subtopic"), all tagged with <paramref name="documentId"/>.
    /// Returns an empty root node on LLM or parse failure (never throws).
    /// </summary>
    public async Task<MindMapNode> ExtractAsync(string text, string documentId, CancellationToken ct)
    {
        var client = _options.ChatClient ?? _chatClient;
        var prompt = _options.Prompt
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{depth}", _options.MaxDepth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        string? responseText;
        try
        {
            var response = await client.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
            responseText = response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLlmCallFailed(_logger, ex);
            return EmptyRoot();
        }

        var root = TryParse(responseText);
        if (root is null)
            return EmptyRoot();

        if (_graphStore is not null)
            await PersistAsync(root, parentName: null, documentId, ct).ConfigureAwait(false);

        return root;
    }

    private async Task PersistAsync(MindMapNode node, string? parentName, string documentId, CancellationToken ct)
    {
        var entity = new GraphEntity(node.Title, "mind_map_node", node.Summary)
        {
            SourceDocumentId = documentId,
        };
        await _graphStore!.AddEntitiesAsync([entity], ct).ConfigureAwait(false);

        if (parentName is not null)
        {
            var rel = new GraphRelationship(parentName, node.Title, "has_subtopic", Weight: 1.0)
            {
                SourceDocumentId = documentId,
            };
            await _graphStore.AddRelationshipsAsync([rel], ct).ConfigureAwait(false);
        }

        foreach (var child in node.Children)
            await PersistAsync(child, node.Title, documentId, ct).ConfigureAwait(false);
    }

    private MindMapNode? TryParse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;
        try
        {
            // Models routinely fence the tree and open with "Here is the mind map:" despite the
            // prompt. This site had no fence handling at all, so every such reply became the
            // empty root below — the same silent-empty shape as the GraphRAG entity outage.
            var json = LlmJsonExtractor.Extract(response, LlmJsonPayloadKind.Object);
            return JsonSerializer.Deserialize<MindMapNode>(json, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            LogJsonParseFailed(_logger, ex);
            return null;
        }
    }

    private static MindMapNode EmptyRoot() => new(string.Empty, string.Empty, []);

    [LoggerMessage(EventId = 1197856777, EventName = "log_llm_call_failed", Level = LogLevel.Warning, Message = "LLM call failed during mind-map extraction")]
    private static partial void LogLlmCallFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 1301217475, EventName = "log_json_parse_failed", Level = LogLevel.Warning, Message = "Failed to parse mind-map JSON from LLM response")]
    private static partial void LogJsonParseFailed(ILogger logger, Exception ex);
}
