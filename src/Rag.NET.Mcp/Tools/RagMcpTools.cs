using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Mcp.Tools;

[McpServerToolType]
public sealed class RagMcpTools(IRagPipeline pipeline)
{
    [McpServerTool(Name = "rag_retrieve")]
    [Description("Retrieve semantically relevant chunks from the RAG pipeline for a given query.")]
    public async Task<string> RetrieveAsync(
        [Description("The natural-language query to search for.")] string query,
        [Description("Maximum number of results to return (default: 5).")] int topK = 5,
        [Description("Whether to use hybrid (keyword + semantic) search (default: true).")] bool useHybrid = true)
    {
        var options = new RetrievalOptions
        {
            TopK = topK,
            UseHybridSearch = useHybrid,
        };

        var result = await pipeline.RetrieveAsync(query, options).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Retrieval failed: {result.Error}");
        return JsonSerializer.Serialize(result.Value);
    }

    [McpServerTool(Name = "rag_ask")]
    [Description("Ask a question and receive a grounded answer generated from the RAG pipeline.")]
    public async Task<string> AskAsync(
        [Description("The natural-language question to answer.")] string query,
        [Description("Maximum number of source chunks to retrieve (default: 5).")] int topK = 5,
        [Description("Whether to use hybrid (keyword + semantic) search (default: true).")] bool useHybrid = true)
    {
        var options = new RagOptions
        {
            TopK = topK,
            UseHybridSearch = useHybrid,
        };

        var response = await pipeline.AskAsync(query, options).ConfigureAwait(false);
        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "rag_ingest")]
    [Description("Ingest a text document into the RAG pipeline.")]
    public async Task<string> IngestAsync(
        [Description("The text content of the document to ingest.")] string content,
        [Description("Optional document ID. A new GUID is generated when omitted.")] string? documentId,
        [Description("Optional file name for the document.")] string? fileName,
        [Description("Optional MIME content type (e.g. \"text/plain\").")] string? contentType,
        [Description("Optional metadata tags as key=value pairs (e.g. [\"author=Alice\", \"topic=AI\"]).")] string[]? tags)
    {
        var resolvedId = documentId ?? Guid.NewGuid().ToString();
        var resolvedFileName = fileName ?? $"{resolvedId}.txt";

        var tagDict = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                var idx = tag.IndexOf('=', StringComparison.Ordinal);
                if (idx > 0)
                    tagDict[tag[..idx]] = tag[(idx + 1)..];
            }
        }

        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId(resolvedId),
            FileName = resolvedFileName,
            ContentType = contentType,
            Tags = tagDict,
        };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var result = await pipeline.IngestAsync(stream, metadata).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"Ingestion failed: {result.Error}");
        return JsonSerializer.Serialize(new { result.Value.DocumentId, result.Value.ChunksStored });
    }
}
