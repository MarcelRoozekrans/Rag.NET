using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class TextDocumentParserTests
{
    private readonly TextDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = new DocumentId("doc-1"),
        FileName = "test.txt"
    };

    [Fact]
    public void CanParse_TextPlain_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/plain"));
    }

    [Fact]
    public void CanParse_ApplicationJson_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("application/json"));
    }

    [Fact]
    public async Task ParseAsync_SimpleText_ReturnsSingleSection()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Hello, world!"));
        var metadata = CreateMetadata();

        var sections = await _sut.ParseAsync(stream, metadata, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Equal("Hello, world!", sections[0].Text);
        Assert.Equal("doc-1", sections[0].DocumentId);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));
        var metadata = CreateMetadata();

        var sections = await _sut.ParseAsync(stream, metadata, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }
}
