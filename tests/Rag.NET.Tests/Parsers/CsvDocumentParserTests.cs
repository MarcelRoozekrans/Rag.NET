using System.Text;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Xunit;

namespace Rag.NET.Tests.Parsers;

public class CsvDocumentParserTests
{
    private readonly CsvDocumentParser _sut = new();

    private static DocumentMetadata CreateMetadata() => new()
    {
        DocumentId = "doc-1",
        FileName = "test.csv"
    };

    [Fact]
    public void CanParse_TextCsv_ReturnsTrue()
    {
        Assert.True(_sut.CanParse("text/csv"));
    }

    [Fact]
    public void CanParse_TextPlain_ReturnsFalse()
    {
        Assert.False(_sut.CanParse("text/plain"));
    }

    [Fact]
    public async Task ParseAsync_BasicCsv_ReturnsRowPerSection()
    {
        var csv = "Name,Age,City\nAlice,30,Amsterdam\nBob,25,Berlin";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Name: Alice | Age: 30 | City: Amsterdam", sections[0].Text);
        Assert.Equal("Name: Bob | Age: 25 | City: Berlin", sections[1].Text);
    }

    [Fact]
    public async Task ParseAsync_SetsDocumentIdAndSectionIndex()
    {
        var csv = "Col\nVal1\nVal2";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(sections, s => Assert.Equal("doc-1", s.DocumentId));
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsNoSections()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_HeaderOnly_ReturnsNoSections()
    {
        var csv = "Name,Age,City";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SkipsEmptyRows()
    {
        var csv = "Name\nAlice\n\nBob";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var sections = await _sut.ParseAsync(stream, CreateMetadata(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sections.Count);
    }
}
