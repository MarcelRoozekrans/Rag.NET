using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>
/// Extracts a hierarchical mind-map tree from document text using an LLM.
/// Optionally persists nodes and edges to an IGraphStore.
/// </summary>
public sealed class MindMapExtractor(IChatClient chatClient, IGraphStore? graphStore, MindMapOptions options)
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Extract a mind-map tree from <paramref name="text"/>. If an IGraphStore was provided,
    /// nodes are written as GraphEntity (Type = "mind_map_node") and edges as GraphRelationship
    /// (Description = "has_subtopic"), all tagged with <paramref name="documentId"/>.
    /// Returns an empty root node on LLM or parse failure (never throws).
    /// </summary>
    public async Task<MindMapNode> ExtractAsync(string text, string documentId, CancellationToken ct)
    {
        var client = options.ChatClient ?? chatClient;
        var prompt = options.Prompt
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{depth}", options.MaxDepth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };

        string? responseText;
        try
        {
            var response = await client.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
            responseText = response.Text;
        }
        catch (InvalidOperationException)
        {
            return EmptyRoot();
        }
        catch (HttpRequestException)
        {
            return EmptyRoot();
        }

        var root = TryParse(responseText);
        if (root is null)
            return EmptyRoot();

        if (graphStore is not null)
            await PersistAsync(root, parentName: null, documentId, ct).ConfigureAwait(false);

        return root;
    }

    private async Task PersistAsync(MindMapNode node, string? parentName, string documentId, CancellationToken ct)
    {
        var entity = new GraphEntity(node.Title, "mind_map_node", node.Summary)
        {
            SourceDocumentId = documentId,
        };
        await graphStore!.AddEntitiesAsync([entity], ct).ConfigureAwait(false);

        if (parentName is not null)
        {
            var rel = new GraphRelationship(parentName, node.Title, "has_subtopic", Weight: 1.0);
            await graphStore.AddRelationshipsAsync([rel], ct).ConfigureAwait(false);
        }

        foreach (var child in node.Children)
            await PersistAsync(child, node.Title, documentId, ct).ConfigureAwait(false);
    }

    private static MindMapNode? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MindMapNode>(json, s_jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MindMapNode EmptyRoot() => new(string.Empty, string.Empty, []);
}
