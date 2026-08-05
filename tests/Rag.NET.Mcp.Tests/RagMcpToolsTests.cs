using System.Text.Json;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Mcp.Tools;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Xunit;
using ZeroAlloc.Results;

namespace Rag.NET.Mcp.Tests;

public sealed class RagMcpToolsTests
{
    private readonly IRagPipeline _pipeline = Substitute.For<IRagPipeline>();
    private readonly RagMcpTools _sut;

    public RagMcpToolsTests()
    {
        _sut = new RagMcpTools(_pipeline);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // RetrieveAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetrieveAsync_CallsPipelineWithCorrectOptions_AndReturnsJsonResults()
    {
        var chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("doc-1"), ChunkIndex = 0 };
        var searchResult = new SearchResult { Chunk = chunk, Score = 0.9 };
        IReadOnlyList<SearchResult> results = [searchResult];

        _pipeline.RetrieveAsync(
                "my query",
                Arg.Is<RetrievalOptions>(o => o!.TopK == 3 && o.UseHybridSearch),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(results)));

        var json = await _sut.RetrieveAsync("my query", topK: 3, useHybrid: true);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task RetrieveAsync_WithDefaultArgs_UsesTopK5AndHybridTrue()
    {
        _pipeline.RetrieveAsync(
                Arg.Any<string>(),
                Arg.Is<RetrievalOptions>(o => o!.TopK == 5 && o.UseHybridSearch),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<SearchResult>, RagError>.Success(
                (IReadOnlyList<SearchResult>)Array.Empty<SearchResult>())));

        await _sut.RetrieveAsync("test");

        _ = await _pipeline.Received(1).RetrieveAsync(
            "test",
            Arg.Is<RetrievalOptions>(o => o!.TopK == 5 && o.UseHybridSearch),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AskAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_CallsPipelineWithCorrectOptions_AndReturnsJsonAnswer()
    {
        var chunk = new TextChunk { Text = "relevant", DocumentId = new DocumentId("doc-2"), ChunkIndex = 0 };
        var response = new RagResponse
        {
            Answer = "42",
            Sources = [new SearchResult { Chunk = chunk, Score = 0.8 }],
        };

        _pipeline.AskAsync(
                "What is the answer?",
                Arg.Is<RagOptions>(o => o!.TopK == 4 && !o.UseHybridSearch),
                Arg.Any<CancellationToken>())
            .Returns(response);

        var json = await _sut.AskAsync("What is the answer?", topK: 4, useHybrid: false);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("42", doc.RootElement.GetProperty("Answer").GetString());
    }

    [Fact]
    public async Task AskAsync_WithDefaultArgs_UsesTopK5AndHybridTrue()
    {
        var response = new RagResponse { Answer = "ok", Sources = [] };

        _pipeline.AskAsync(
                Arg.Any<string>(),
                Arg.Is<RagOptions>(o => o!.TopK == 5 && o.UseHybridSearch),
                Arg.Any<CancellationToken>())
            .Returns(response);

        await _sut.AskAsync("question");

        await _pipeline.Received(1).AskAsync(
            "question",
            Arg.Is<RagOptions>(o => o!.TopK == 5 && o.UseHybridSearch),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IngestAsync
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IngestAsync_CallsPipelineWithParsedMetadata_AndReturnsJsonResult()
    {
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Is<DocumentMetadata>(m =>
                    m!.DocumentId.Equals(new DocumentId("doc-42")) &&
                    m.FileName == "report.txt" &&
                    m.ContentType == "text/plain" &&
                    m.Tags["author"] == "Alice"),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("doc-42"), ChunksStored = 5 })));

        var json = await _sut.IngestAsync(
            content: "document body",
            documentId: "doc-42",
            fileName: "report.txt",
            contentType: "text/plain",
            tags: ["author=Alice"]);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("doc-42", doc.RootElement.GetProperty("DocumentId").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("ChunksStored").GetInt32());
    }

    [Fact]
    public async Task IngestAsync_WithNullDocumentId_GeneratesGuid()
    {
        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Any<DocumentMetadata>(),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var meta = ci.ArgAt<DocumentMetadata>(1);
                return Task.FromResult(Result<IngestionResult, RagError>.Success(
                    new IngestionResult { DocumentId = meta.DocumentId, ChunksStored = 1 }));
            });

        var json = await _sut.IngestAsync("text", null, null, null, null);

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("DocumentId").GetString();
        Assert.True(Guid.TryParse(id, out _), $"Expected GUID but got: {id}");
    }

    [Fact]
    public async Task IngestAsync_WithMultipleTags_ParsesAllKeyValuePairs()
    {
        DocumentMetadata? capturedMetadata = null;

        _pipeline.IngestAsync(
                Arg.Any<Stream>(),
                Arg.Do<DocumentMetadata>(m => capturedMetadata = m),
                Arg.Any<IngestionOptions?>(),
                Arg.Any<IProgress<IngestionProgress>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IngestionResult, RagError>.Success(
                new IngestionResult { DocumentId = new DocumentId("x"), ChunksStored = 0 })));

        await _sut.IngestAsync("text", "doc-1", "file.txt", null, ["key1=val1", "key2=val2", "malformed"]);

        Assert.NotNull(capturedMetadata);
        Assert.Equal("val1", capturedMetadata.Tags["key1"]);
        Assert.Equal("val2", capturedMetadata.Tags["key2"]);
        Assert.False(capturedMetadata.Tags.ContainsKey("malformed"));
    }
}
