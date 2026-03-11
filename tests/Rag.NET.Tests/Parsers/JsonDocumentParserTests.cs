using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class JsonDocumentParserTests
{
    private readonly JsonDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.json"
    };

    [Fact]
    public void CanParse_ApplicationJson_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("application/json"));
    }

    [Fact]
    public void CanParse_TextPlain_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_Array_ReturnsSectionPerElement()
    {
        var json = """[{"name":"Alice"},{"name":"Bob"}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Contains("Alice", sections[0].Text, StringComparison.Ordinal);
        Assert.Contains("Bob", sections[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SingleObject_ReturnsSingleSection()
    {
        var json = """{"name":"Alice","age":30}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(sections);
        Assert.Contains("Alice", sections[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var json = """[{"a":1},{"b":2}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyArray_ReturnsNoSections()
    {
        var json = "[]";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }
}
