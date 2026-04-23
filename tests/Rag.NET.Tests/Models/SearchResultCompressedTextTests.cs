using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Models;

public class SearchResultCompressedTextTests
{
    [Fact]
    public void CompressedText_DefaultsToNull()
    {
        var sut = new SearchResult
        {
            Chunk = new TextChunk { Text = "hello", DocumentId = new DocumentId("d"), ChunkIndex = 0 },
            Score = 0.5,
        };

        Assert.Null(sut.CompressedText);
    }

    [Fact]
    public void CompressedText_CanBeSetViaInit()
    {
        var sut = new SearchResult
        {
            Chunk = new TextChunk { Text = "hello world", DocumentId = new DocumentId("d"), ChunkIndex = 0 },
            Score = 0.5,
            CompressedText = "hello",
        };

        Assert.Equal("hello", sut.CompressedText);
    }
}
