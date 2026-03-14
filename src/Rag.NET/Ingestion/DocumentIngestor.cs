using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Search;
using ZInject;

namespace Rag.NET.Ingestion;

/// <summary>
/// Base ingestor that parses, chunks, embeds, and stores documents.
/// </summary>
[Singleton(As = typeof(IIngestor))]
public sealed class DocumentIngestor(
    IEnumerable<IDocumentParser> parsers,
    IChunkingStrategy chunkingStrategy,
    IVectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ChunkingOptions chunkingOptions,
    InMemoryBm25Index bm25Index) : IIngestor
{
    private int _nextBm25DocId;

    public async Task<IngestionResult> IngestAsync(
        Stream document,
        DocumentMetadata metadata,
        IngestionOptions? options = null,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parser = parsers.FirstOrDefault(p => p.CanParse(metadata.ContentType ?? "text/plain"))
            ?? throw new InvalidOperationException(
                $"No parser registered for content type '{metadata.ContentType}'.");

        if (options?.Overwrite == true)
        {
            await vectorStore.DeleteByDocumentIdAsync(metadata.DocumentId, cancellationToken).ConfigureAwait(false);
            bm25Index.Remove(metadata.DocumentId);
        }

        var chunks = await ParseAndChunkAsync(parser, document, metadata, cancellationToken).ConfigureAwait(false);

        ReportProgress(progress, IngestionProgressStage.Parsing, metadata.DocumentId, null, null, "Parsing complete");
        ApplyMetadataTags(chunks, metadata);
        ReportProgress(progress, IngestionProgressStage.Chunking, metadata.DocumentId, chunks.Count, chunks.Count, $"Chunked into {chunks.Count} chunks");

        if (chunks.Count == 0)
            return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = 0 };

        var texts = chunks.Select(c => c.Text).ToList();
        var embeddings = await embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var embeddedChunks = chunks
            .Zip(embeddings, (chunk, embedding) => new EmbeddedChunk { Chunk = chunk, Embedding = embedding.Vector })
            .ToList();

        ReportProgress(progress, IngestionProgressStage.Embedding, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Generated {embeddedChunks.Count} embeddings");
        await vectorStore.StoreAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
        ReportProgress(progress, IngestionProgressStage.Storing, metadata.DocumentId, embeddedChunks.Count, embeddedChunks.Count, $"Stored {embeddedChunks.Count} chunks");

        foreach (ref readonly var ec in CollectionsMarshal.AsSpan(embeddedChunks))
        {
            var id = System.Threading.Interlocked.Increment(ref _nextBm25DocId);
            bm25Index.Add(id, ec.Chunk);
        }

        return new IngestionResult { DocumentId = metadata.DocumentId, ChunksStored = embeddedChunks.Count };
    }

    public async Task DeleteAsync(string documentId, CancellationToken cancellationToken = default)
    {
        await vectorStore.DeleteByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false);
        bm25Index.Remove(documentId);
    }

    private async Task<List<TextChunk>> ParseAndChunkAsync(
        IDocumentParser parser,
        Stream document,
        DocumentMetadata metadata,
        CancellationToken cancellationToken)
    {
        var chunks = new List<TextChunk>();
        var headingBreadcrumbs = new string?[6];

        await foreach (var section in parser.ParseAsync(document, metadata, cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string>? headingMetadata = null;

            if (section.HeadingLevel is { } level && level >= 1 && level <= 6 && section.Heading is not null)
            {
                headingBreadcrumbs[level - 1] = section.Heading;
                var idx = level;
                while (idx < 6)
                {
                    headingBreadcrumbs[idx] = null;
                    idx++;
                }

                var parts = new List<string>(level);
                foreach (var h in headingBreadcrumbs[..level])
                {
                    if (h is not null)
                        parts.Add(h);
                }

                var breadcrumb = string.Join(" > ", parts);
                headingMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["heading"] = section.Heading,
                    ["heading_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["heading_breadcrumb"] = breadcrumb,
                };
            }

            await foreach (var chunk in chunkingStrategy.ChunkAsync(section, chunkingOptions, cancellationToken).ConfigureAwait(false))
            {
                if (headingMetadata is not null)
                {
                    foreach (var kv in headingMetadata)
                        chunk.Metadata.TryAdd(kv.Key, kv.Value);
                }

                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private static void ApplyMetadataTags(List<TextChunk> chunks, DocumentMetadata metadata)
    {
        foreach (ref var chunk in CollectionsMarshal.AsSpan(chunks))
        {
            foreach (var tag in metadata.Tags)
                chunk.Metadata.TryAdd(tag.Key, tag.Value);
            chunk.Metadata.TryAdd("document_id", metadata.DocumentId);
            chunk.Metadata.TryAdd("file_name", metadata.FileName);
        }
    }

    private static void ReportProgress(
        IProgress<IngestionProgress>? progress,
        IngestionProgressStage stage,
        string documentId,
        int? current,
        int? total,
        string message)
    {
        progress?.Report(new IngestionProgress
        {
            Stage = stage,
            DocumentId = documentId,
            Current = current,
            Total = total,
            Message = message,
        });
    }
}
