using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Parsers;
using Rag.NET.Chunking;
using Rag.NET.Pipeline;
using Xunit;

namespace Rag.NET.Tests.Pipeline;

public class RagPipelineHeadingIntegrationTests
{
    private static RagPipeline BuildPipeline(IVectorStore vectorStore, IEmbeddingGenerator<string, Embedding<float>> embedder) =>
        new(
            [new MarkdownDocumentParser()],
            new FixedSizeChunkingStrategy(),
            vectorStore,
            embedder,
            chatClient: null,
            new ChunkingOptions { MaxChunkSize = 512, Overlap = 0 });

    private static IEmbeddingGenerator<string, Embedding<float>> BuildEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var texts = ci.Arg<IEnumerable<string>>().ToList();
                var list = texts.Select(_ => new Embedding<float>(new float[3])).ToList();
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
            });
        return embedder;
    }

    private static EmbeddedChunk FindChunkByHeading(List<EmbeddedChunk> captured, string heading) =>
        captured.First(c =>
            c.Chunk.Metadata.TryGetValue("heading", out var h) &&
            string.Equals(h, heading, StringComparison.Ordinal));

    [Fact]
    public async Task IngestAsync_MarkdownWithNestedHeadings_ProducesBreadcrumbs()
    {
        var markdown = """
            # Chapter 1

            Intro content.

            ## Section 1.1

            Sub content.

            ### Deep 1.1.1

            Deep content.

            # Chapter 2

            New chapter content.
            """;

        var vectorStore = Substitute.For<IVectorStore>();
        List<EmbeddedChunk> captured = [];
        await vectorStore.StoreAsync(
            Arg.Do<IReadOnlyList<EmbeddedChunk>>(c => captured.AddRange(c)),
            Arg.Any<CancellationToken>());

        var pipeline = BuildPipeline(vectorStore, BuildEmbedder());
        var meta = new DocumentMetadata { DocumentId = "doc-1", FileName = "test.md", ContentType = "text/markdown" };

        await pipeline.IngestAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown)),
            meta,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(captured);

        var chapterChunk = FindChunkByHeading(captured, "Chapter 1");
        Assert.Equal("1", chapterChunk.Chunk.Metadata["heading_level"]);
        Assert.Equal("Chapter 1", chapterChunk.Chunk.Metadata["heading_breadcrumb"]);

        var sectionChunk = FindChunkByHeading(captured, "Section 1.1");
        Assert.Equal("Chapter 1 > Section 1.1", sectionChunk.Chunk.Metadata["heading_breadcrumb"]);

        var deepChunk = FindChunkByHeading(captured, "Deep 1.1.1");
        Assert.Equal("Chapter 1 > Section 1.1 > Deep 1.1.1", deepChunk.Chunk.Metadata["heading_breadcrumb"]);

        var chapter2Chunk = FindChunkByHeading(captured, "Chapter 2");
        Assert.Equal("Chapter 2", chapter2Chunk.Chunk.Metadata["heading_breadcrumb"]);
    }
}
