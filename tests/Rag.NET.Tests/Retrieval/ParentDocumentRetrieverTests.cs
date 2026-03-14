using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Retrieval;

public class ParentDocumentRetrieverTests
{
    private readonly IRetriever _inner = Substitute.For<IRetriever>();
    private readonly InMemoryParentChunkStore _parentStore = new();

    private ParentDocumentRetriever CreateSut() => new(_inner, _parentStore);

    [Fact]
    public async Task RetrieveAsync_ReplacesChildTextWithParentText()
    {
        _parentStore.Add("doc1", 0, "full parent text that is much larger");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "small child",
                    DocumentId = "doc1",
                    ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["_parentKey"] = "doc1:0"
                    }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("full parent text that is much larger", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_DeduplicatesChildrenSharingParent()
    {
        _parentStore.Add("doc1", 0, "parent text");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child A", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child B", DocumentId = "doc1", ChunkIndex = 1,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.7
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal("parent text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_UsesMaxChildScore()
    {
        _parentStore.Add("doc1", 0, "parent text");

        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child A", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.7
            },
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child B", DocumentId = "doc1", ChunkIndex = 1,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.9, results[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_WhenOptedOut_ReturnsChildChunks()
    {
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:0" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var opts = new RetrievalOptions { UseParentDocument = false };
        var results = await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenParentNotFound_ReturnsChildChunk()
    {
        // No parent stored — should fall back to child
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["_parentKey"] = "doc1:99" }
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_WhenNoParentKey_ReturnsChildChunk()
    {
        // Child has no _parentKey metadata — should pass through unchanged
        var childResults = new List<SearchResult>
        {
            new()
            {
                Chunk = new TextChunk
                {
                    Text = "child text", DocumentId = "doc1", ChunkIndex = 0,
                },
                Score = 0.9
            }
        };
        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(childResults);

        var sut = CreateSut();
        var results = await sut.RetrieveAsync("query", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("child text", results[0].Chunk.Text);
    }

    [Fact]
    public async Task RetrieveAsync_OverFetchesToCompensateForDeduplication()
    {
        _parentStore.Add("doc1", 0, "parent text");

        _inner.RetrieveAsync(Arg.Any<string>(), Arg.Any<RetrievalOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SearchResult>());

        var sut = CreateSut();
        var opts = new RetrievalOptions { TopK = 5 };
        await sut.RetrieveAsync("query", opts, TestContext.Current.CancellationToken);

        // Verify inner was called with higher TopK to compensate for deduplication
        await _inner.Received(1).RetrieveAsync(
            "query",
            Arg.Is<RetrievalOptions>(o => o.TopK > 5 && !o.UseParentDocument),
            Arg.Any<CancellationToken>());
    }
}
