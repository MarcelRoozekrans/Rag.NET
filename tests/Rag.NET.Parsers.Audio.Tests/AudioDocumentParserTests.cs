using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Whisper.net;
using Whisper.net.Ggml;
using Xunit;

namespace Rag.NET.Parsers.Audio.Tests;

file sealed class FakeAudioDocumentParser(
    AudioParserOptions options,
    IReadOnlyList<SegmentData> fakeSegments) : AudioDocumentParser(options)
{
    protected override IAsyncEnumerable<SegmentData> TranscribeAsync(
        string audioFilePath, CancellationToken ct) =>
        fakeSegments.ToAsyncEnumerable();
}

public class AudioDocumentParserTests
{
    private static readonly AudioParserOptions DefaultOptions = new();
    private static readonly DocumentMetadata TestMetadata = new()
    {
        DocumentId = new DocumentId("test-doc"),
        FileName = "test.wav",
        ContentType = "audio/wav",
    };

    private static SegmentData MakeSegment(string text, TimeSpan start, TimeSpan end) =>
        new(text, start, end, 0f, 0f, 0f, 0f, "en", []);

    [Fact]
    public void AddAudioParser_RegistersParserInDI()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddAudioParser();

        var provider = services.BuildServiceProvider();
        var parsers = provider.GetServices<IDocumentParser>();
        Assert.Contains(parsers, p => p is AudioDocumentParser);
    }

    [Fact]
    public void AddAudioParser_RegistersCustomOptions()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);
        var customOptions = new AudioParserOptions { ModelType = GgmlType.Medium, Language = "fr" };

        builder.AddAudioParser(customOptions);

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<AudioParserOptions>();
        Assert.Equal(GgmlType.Medium, resolved.ModelType);
        Assert.Equal("fr", resolved.Language);
    }

    [Fact]
    public void AddAudioParser_DefaultOptions_WhenNoneProvided()
    {
        var services = new ServiceCollection();
        var builder = new RagBuilder(services);

        builder.AddAudioParser();

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<AudioParserOptions>();
        Assert.Equal(GgmlType.Base, resolved.ModelType);
        Assert.Null(resolved.Language);
    }

    [Fact]
    public void AudioParserOptions_Defaults_AreCorrect()
    {
        var opts = new AudioParserOptions();
        Assert.Equal(GgmlType.Base, opts.ModelType);
        Assert.Null(opts.Language);
        Assert.Equal(Path.GetTempPath(), opts.ModelCacheDirectory);
    }

    [Theory]
    [InlineData("audio/wav",  true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/flac", true)]
    [InlineData("audio/ogg",  true)]
    [InlineData("audio/mp4",  true)]
    [InlineData("application/pdf",  false)]
    [InlineData("text/plain",       false)]
    [InlineData("application/json", false)]
    public void CanParse_VariousContentTypes_ReturnsExpected(string contentType, bool expected)
    {
        var sut = new AudioDocumentParser(new AudioParserOptions());
        Assert.Equal(expected, sut.CanParse(contentType));
    }

    [Fact]
    public async Task ParseAsync_Segments_YieldsOneSectionPerNonWhitespaceSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var segments = new[]
        {
            MakeSegment(" Hello world ", TimeSpan.Zero, TimeSpan.FromSeconds(3)),
            MakeSegment(" Goodbye ", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)),
        };

        var sut = new FakeAudioDocumentParser(DefaultOptions, segments);
        var sections = await sut.ParseAsync(Stream.Null, TestMetadata, ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Hello world", sections[0].Text);
        Assert.Equal("Goodbye", sections[1].Text);
        Assert.Equal(TestMetadata.DocumentId, sections[0].DocumentId);
        Assert.Equal(TestMetadata.DocumentId, sections[1].DocumentId);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_WhitespaceOnlySegment_IsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var segments = new[]
        {
            MakeSegment("First", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            MakeSegment("   ", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            MakeSegment("Third", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)),
        };

        var sut = new FakeAudioDocumentParser(DefaultOptions, segments);
        var sections = await sut.ParseAsync(Stream.Null, TestMetadata, ct).ToListAsync(ct);

        Assert.Equal(2, sections.Count);
        Assert.Equal("First", sections[0].Text);
        Assert.Equal(0, sections[0].SectionIndex);
        Assert.Equal("Third", sections[1].Text);
        Assert.Equal(1, sections[1].SectionIndex);
    }

    [Fact]
    public async Task ParseAsync_EmptySegments_ReturnsNoSections()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new FakeAudioDocumentParser(DefaultOptions, []);
        var sections = await sut.ParseAsync(Stream.Null, TestMetadata, ct).ToListAsync(ct);

        Assert.Empty(sections);
    }

    [Fact]
    public async Task ParseAsync_SetsTimestampHeading()
    {
        var ct = TestContext.Current.CancellationToken;
        var segments = new[]
        {
            MakeSegment("Hello", TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(5200)),
        };

        var sut = new FakeAudioDocumentParser(DefaultOptions, segments);
        var sections = await sut.ParseAsync(Stream.Null, TestMetadata, ct).ToListAsync(ct);

        Assert.Single(sections);
        Assert.Equal("00:01.500 - 00:05.200", sections[0].Heading);
    }
}
