using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Rag.NET.Models;
using Rag.NET.Parsers;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Benchmarks;

[MemoryDiagnoser]
public class ParserBenchmarks
{
    private byte[] _smallText = null!;
    private byte[] _largeText = null!;
    private byte[] _smallHtml = null!;
    private byte[] _largeHtml = null!;
    private byte[] _csvData = null!;
    private byte[] _jsonData = null!;

    private readonly TextDocumentParser _textParser = new();
    private readonly MarkdownDocumentParser _markdownParser = new();
    private readonly HtmlDocumentParser _htmlParser = new();
    private readonly CsvDocumentParser _csvParser = new();
    private readonly JsonDocumentParser _jsonParser = new();

    private static readonly DocumentMetadata TextMeta = new()
    {
        DocumentId = "bench", FileName = "bench.txt", ContentType = "text/plain",
    };

    private static readonly DocumentMetadata MarkdownMeta = new()
    {
        DocumentId = "bench", FileName = "bench.md", ContentType = "text/markdown",
    };

    private static readonly DocumentMetadata HtmlMeta = new()
    {
        DocumentId = "bench", FileName = "bench.html", ContentType = "text/html",
    };

    private static readonly DocumentMetadata CsvMeta = new()
    {
        DocumentId = "bench", FileName = "bench.csv", ContentType = "text/csv",
    };

    private static readonly DocumentMetadata JsonMeta = new()
    {
        DocumentId = "bench", FileName = "bench.json", ContentType = "application/json",
    };

    [GlobalSetup]
    public void Setup()
    {
        _smallText = Encoding.UTF8.GetBytes(GenerateText(1_000));
        _largeText = Encoding.UTF8.GetBytes(GenerateText(100_000));
        _smallHtml = Encoding.UTF8.GetBytes(GenerateHtml(5));
        _largeHtml = Encoding.UTF8.GetBytes(GenerateHtml(100));
        _csvData = Encoding.UTF8.GetBytes(GenerateCsv(500));
        _jsonData = Encoding.UTF8.GetBytes(GenerateJson(100));
    }

    [Benchmark]
    public Task<int> Text_Small() => CountSections(_textParser, _smallText, TextMeta);

    [Benchmark]
    public Task<int> Text_Large() => CountSections(_textParser, _largeText, TextMeta);

    [Benchmark]
    public Task<int> Markdown_Small() => CountSections(_markdownParser, _smallText, MarkdownMeta);

    [Benchmark]
    public Task<int> Markdown_Large() => CountSections(_markdownParser, _largeText, MarkdownMeta);

    [Benchmark]
    public Task<int> Html_Small() => CountSections(_htmlParser, _smallHtml, HtmlMeta);

    [Benchmark]
    public Task<int> Html_Large() => CountSections(_htmlParser, _largeHtml, HtmlMeta);

    [Benchmark]
    public Task<int> Csv_500Rows() => CountSections(_csvParser, _csvData, CsvMeta);

    [Benchmark]
    public Task<int> Json_100Elements() => CountSections(_jsonParser, _jsonData, JsonMeta);

    private static async Task<int> CountSections(
        Abstractions.IDocumentParser parser, byte[] data, DocumentMetadata metadata)
    {
        using var stream = new MemoryStream(data);
        int count = 0;
        await foreach (var _ in parser.ParseAsync(stream, metadata))
        {
            count++;
        }

        return count;
    }

    private static string GenerateText(int approximateLength)
    {
        const string paragraph = "The quick brown fox jumps over the lazy dog. " +
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit.\n\n";

        var sb = new StringBuilder(approximateLength + paragraph.Length);
        while (sb.Length < approximateLength)
        {
            sb.Append(paragraph);
        }

        return sb.ToString();
    }

    private static string GenerateHtml(int sectionCount)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><body>");
        for (int i = 0; i < sectionCount; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<h2>Section {i}</h2>");
            sb.Append("<p>Lorem ipsum dolor sit amet, consectetur adipiscing elit. ");
            sb.Append("Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.</p>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string GenerateCsv(int rowCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Age,City,Country");
        for (int i = 0; i < rowCount; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Person{i},{20 + (i % 50)},City{i % 10},Country{i % 5}");
        }

        return sb.ToString();
    }

    private static string GenerateJson(int elementCount)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < elementCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture, $"{{\"id\":{i},\"name\":\"Item {i}\",\"description\":\"Description for item {i}\"}}");
        }

        sb.Append(']');
        return sb.ToString();
    }
}
