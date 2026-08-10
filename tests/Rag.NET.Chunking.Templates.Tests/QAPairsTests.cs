using Rag.NET.Chunking.Templates;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using System.Text;
using Xunit;

namespace Rag.NET.Chunking.Templates.Tests;

public class QAPairsTests
{
    private static readonly DocumentMetadata Metadata =
        new() { DocumentId = new DocumentId("doc.csv"), FileName = "doc.csv" };

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task QAPairsDocumentParser_ParsesCsvRows()
    {
        var csv = "question,answer\nWhat is the capital of France?,Paris\nWhat is 2+2?,4";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());

        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, Metadata, TestContext.Current.CancellationToken))
            sections.Add(section);

        Assert.Equal(2, sections.Count);
        Assert.Equal("What is the capital of France?", sections[0].Text);
    }

    [Fact]
    public async Task QAPairsDocumentParser_CanParseCsv()
    {
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());
        Assert.True(parser.CanParse("text/csv"));
    }

    /// <summary>
    /// All three legitimate claims survive Phase 3.11 and the greedy one does not.
    /// <c>application/octet-stream</c> means "unknown binary"; answering it sent random bytes into
    /// the CSV reader, which threw <c>Cannot resolve question/answer columns</c> out of the
    /// caller's whole document parse. The <c>true</c> rows are the guard against over-correcting
    /// the removal into a narrower parser.
    /// </summary>
    [Theory]
    [InlineData("text/csv", true)]
    [InlineData("application/vnd.ms-excel", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("application/octet-stream", false)]
    public void CanParse_ClaimsTabularTypesButNotUnknownBinary(string contentType, bool expected)
    {
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());
        Assert.Equal(expected, parser.CanParse(contentType));
    }

    [Fact]
    public async Task QAPairsChunkingStrategy_StoresAnswerInMetadata()
    {
        var strategy = new QAPairsChunkingStrategy();
        var sections = new[]
        {
            new DocumentSection { Text = "What is Paris?", Heading = "Capital of France", DocumentId = new DocumentId("doc"), SectionIndex = 0 },
        };

        var chunks = new List<TextChunk>();
        await foreach (var chunk in strategy.ChunkDocumentAsync(ToAsync(sections), new ChunkingOptions(), TestContext.Current.CancellationToken))
            chunks.Add(chunk);

        var single = Assert.Single(chunks);
        Assert.Equal("What is Paris?", single.Text);
        Assert.Equal<MetadataValue>("Capital of France", single.Metadata["answer"]);
        Assert.Equal<MetadataValue>("qa_pairs", single.Metadata["template"]);
    }

    [Fact]
    public async Task QAPairsDocumentParser_SkipsRowsMissingQuestion()
    {
        var csv = "question,answer\n,Paris\nWhat is 2+2?,4";
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parser = new QAPairsDocumentParser(new QAPairsChunkingOptions());

        var sections = new List<DocumentSection>();
        await foreach (var section in parser.ParseAsync(stream, Metadata, TestContext.Current.CancellationToken))
            sections.Add(section);

        Assert.Single(sections);
    }
}
