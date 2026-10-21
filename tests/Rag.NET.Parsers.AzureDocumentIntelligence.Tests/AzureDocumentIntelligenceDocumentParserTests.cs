using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Rag.NET.Models;
using Rag.NET.Testing;
using Xunit;
using DocumentSection = Rag.NET.Models.DocumentSection;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// Cassette-driven coverage of the parser's section mapping: each test feeds one hand-written
/// analyze result through the real SDK and asserts on the sections emitted.
/// </summary>
/// <remarks>
/// Each scenario gets its own <c>modelId</c>, which is part of the analyze path, so the mappings
/// share one cassette directory with the analyzer tests without colliding.
/// </remarks>
[Collection("WireMock")]
public sealed class AzureDocumentIntelligenceDocumentParserTests
{
    private const string FileName = "scan.tiff";

    private readonly WireMockServerFixture _fixture;

    public AzureDocumentIntelligenceDocumentParserTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("AzureDocumentIntelligence");
        _fixture.Server.ResetLogEntries();
    }

    [Fact]
    public async Task ParseAsync_ThreeFrameTiff_EmitsPagesOneToThreeInOrder()
    {
        var sections = await ParseAsync("tiff-three-frames");

        Assert.Equal([1, 2, 3], sections.Select(s => s.PageNumber));
        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
        Assert.Equal("Frame one first paragraph.\n\nFrame one second paragraph.", sections[0].Text);
        Assert.Equal("Frame two text.", sections[1].Text);
        Assert.Equal("Frame three text.", sections[2].Text);
        Assert.All(sections, s => Assert.Equal(new DocumentId("doc-1"), s.DocumentId));
        // prebuilt-read assigns no roles, so no heading is inferred.
        Assert.All(sections, s => Assert.Null(s.Heading));
        Assert.All(sections, s => Assert.Null(s.HeadingLevel));
    }

    [Fact]
    public async Task ParseAsync_DocxWithoutLines_UsesParagraphsNotWords()
    {
        // Office results carry words but no lines and no bounding regions. Building text from
        // words would flatten every paragraph into one run; paragraphs keep the structure.
        var sections = await ParseAsync("docx-paragraphs");

        Assert.Equal(2, sections.Count);
        Assert.Equal(
            "The quarterly report covers revenue, costs and the outlook for next year.",
            sections[0].Text);
        Assert.Equal(
            "Revenue grew by twelve percent compared with the same quarter last year.",
            sections[1].Text);
        Assert.All(sections, s => Assert.Null(s.PageNumber));
        Assert.Equal([0, 1], sections.Select(s => s.SectionIndex));

        // Joined the way a downstream chunker sees the document: paragraph breaks survive.
        var joined = string.Join("\n\n", sections.Select(s => s.Text));
        Assert.Contains("\n\n", joined, StringComparison.Ordinal);
        Assert.Contains(
            "Revenue grew by twelve percent compared with the same quarter last year.",
            joined,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_LayoutRoles_SetHeadingsAndDropPageFurniture()
    {
        var sections = await ParseAsync("layout-roles");

        Assert.Equal(3, sections.Count);

        // Page 1 opens with the title, so the title heads it. Header and page number are gone.
        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal("Annual Report 2026\n\nIntroduction body text.", sections[0].Text);
        Assert.Equal("Annual Report 2026", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);

        // Page 2 starts under the title; its mid-page section heading applies from the next section.
        Assert.Equal(2, sections[1].PageNumber);
        Assert.Equal(
            "Still under the title.\n\nFinancial Results\n\nResults body text.",
            sections[1].Text);
        Assert.Equal("Annual Report 2026", sections[1].Heading);
        Assert.Equal(1, sections[1].HeadingLevel);

        Assert.Equal(3, sections[2].PageNumber);
        Assert.Equal("Results continued.", sections[2].Text);
        Assert.Equal("Financial Results", sections[2].Heading);
        Assert.Equal(2, sections[2].HeadingLevel);

        Assert.All(sections, s => Assert.DoesNotContain("Contoso Annual Report", s.Text, StringComparison.Ordinal));
        Assert.All(sections, s => Assert.DoesNotContain("Confidential", s.Text, StringComparison.Ordinal));
        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task ParseAsync_EmptyResult_EmitsNothingAndRecordsNoCost()
    {
        var ledger = new FakeCostLedger();

        var sections = await ParseAsync("empty", ledger);

        Assert.Empty(sections);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task ParseAsync_ParagraphSpanningTwoPages_AppearsOnceOnTheFirst()
    {
        const string spanning = "A sentence that starts on page one and ends on page two.";

        var sections = await ParseAsync("paragraph-spans-pages");

        Assert.Equal(2, sections.Count);
        Assert.Equal($"Page one opening.\n\n{spanning}", sections[0].Text);
        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal("Page two closing.", sections[1].Text);
        Assert.Equal(2, sections[1].PageNumber);
        Assert.Single(sections, s => s.Text.Contains(spanning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParseAsync_NoParagraphs_FallsBackToPageLines()
    {
        // The analyzer suite's two-page cassette has lines and no paragraphs.
        var sections = await ParseAsync("two-pages");

        Assert.Equal(["PAGE ONE TEXT", "PAGE TWO TEXT"], sections.Select(s => s.Text), StringComparer.Ordinal);
        Assert.Equal([1, 2], sections.Select(s => s.PageNumber));
        Assert.Equal([0, 1], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task ParseAsync_NoParagraphsOrLines_FallsBackToPageWords()
    {
        var sections = await ParseAsync("words-only");

        var section = Assert.Single(sections);
        Assert.Equal("ALPHA BRAVO", section.Text);
        Assert.Equal(1, section.PageNumber);
    }

    [Fact]
    public void CanParse_MatchesMediaTypeIgnoringCaseAndParameters()
    {
        var sut = CreateParser("unused");

        Assert.True(sut.CanParse("IMAGE/TIFF"));
        Assert.True(sut.CanParse("image/tiff; x=y"));
        Assert.True(sut.CanParse("image/heic"));
        Assert.False(sut.CanParse("application/pdf"));
        Assert.False(sut.CanParse(""));
    }

    [Fact]
    public void CanParse_OptedInFormats_AcceptTheirContentTypes()
    {
        var options = new AzureDocumentIntelligenceParserOptions
        {
            Formats = AzureDocumentIntelligenceFormats.Pdf | AzureDocumentIntelligenceFormats.Office,
        };
        var sut = CreateParser("unused", options: options);

        Assert.True(sut.CanParse("application/pdf"));
        Assert.True(sut.CanParse(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document; charset=binary"));
        Assert.True(sut.CanParse("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        Assert.True(sut.CanParse("application/vnd.openxmlformats-officedocument.presentationml.presentation"));
        Assert.False(sut.CanParse("image/tiff"));
        Assert.False(sut.CanParse("text/html"));
    }

    [Fact]
    public void ContentTypes_EachFlag_MapsToItsDocumentedTypes()
    {
        Assert.Equal(["image/tiff"], AzureDocumentIntelligenceFormats.Tiff.ContentTypes());
        Assert.Equal(["image/heif", "image/heic"], AzureDocumentIntelligenceFormats.Heif.ContentTypes());
        Assert.Equal(["application/pdf"], AzureDocumentIntelligenceFormats.Pdf.ContentTypes());
        Assert.Equal(
            ["image/png", "image/jpeg", "image/jpg", "image/bmp"],
            AzureDocumentIntelligenceFormats.Images.ContentTypes());
        Assert.Equal(["text/html"], AzureDocumentIntelligenceFormats.Html.ContentTypes());
        Assert.Equal(3, AzureDocumentIntelligenceFormats.Office.ContentTypes().Count);
        Assert.Empty(AzureDocumentIntelligenceFormats.None.ContentTypes());
    }

    [Fact]
    public void Options_Defaults_AreTiffAndHeifWithA500MegabyteCap()
    {
        var options = new AzureDocumentIntelligenceParserOptions();

        Assert.Equal(AzureDocumentIntelligenceFormats.Tiff | AzureDocumentIntelligenceFormats.Heif, options.Formats);
        Assert.Equal(500L * 1024 * 1024, options.MaxBytes);
        Assert.Equal("prebuilt-read", options.Analyzer.ModelId);
    }

    [Fact]
    public async Task ParseAsync_ServiceFails_ThrowsAzureDocumentIntelligenceExceptionNamingTheFile()
    {
        var exception = await Assert.ThrowsAsync<AzureDocumentIntelligenceException>(
            () => ParseAsync("unauthorized"));

        Assert.Equal(FileName, exception.FileName);
        Assert.Contains(FileName, exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(401, inner.Status);
    }

    [Fact]
    public async Task ParseAsync_Cancelled_PropagatesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = CreateParser("tiff-three-frames");
        using var stream = Content();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, Metadata(), cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task ParseAsync_DoesNotDisposeTheStream()
    {
        var sut = CreateParser("tiff-three-frames");
        using var stream = Content();

        await foreach (var _ in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
        {
        }

        Assert.True(stream.CanRead);
    }

    private async Task<List<DocumentSection>> ParseAsync(string modelId, FakeCostLedger? ledger = null)
    {
        var sut = CreateParser(modelId, ledger);
        using var stream = Content();
        var sections = new List<DocumentSection>();
        await foreach (var section in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        return sections;
    }

    private AzureDocumentIntelligenceDocumentParser CreateParser(
        string modelId,
        FakeCostLedger? costLedger = null,
        AzureDocumentIntelligenceParserOptions? options = null)
    {
        options ??= new AzureDocumentIntelligenceParserOptions();
        options.Analyzer.ModelId = modelId;
        options.Analyzer.PollingInterval = TimeSpan.Zero;

        var analyzer = new AzureDocumentIntelligenceAnalyzer(
            new Uri(_fixture.BaseUrl),
            new AzureKeyCredential("cassette-key"),
            options.Analyzer,
            costLedger,
            logger: null,
            NoWaitingClientOptions());

        return new AzureDocumentIntelligenceDocumentParser(analyzer, options);
    }

    /// <summary>
    /// Removes Azure.Core's retry delay and its 100-second network timeout, keeping the retries
    /// so a transient loopback reset on a cold run stays recoverable.
    /// </summary>
    private static DocumentIntelligenceClientOptions NoWaitingClientOptions()
    {
        var clientOptions = new DocumentIntelligenceClientOptions();
        clientOptions.Retry.Mode = RetryMode.Fixed;
        clientOptions.Retry.MaxRetries = 3;
        clientOptions.Retry.Delay = TimeSpan.Zero;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        return clientOptions;
    }

    private static MemoryStream Content() => new([0x49, 0x49, 0x2A, 0x00]);

    private static DocumentMetadata Metadata() => new()
    {
        DocumentId = new DocumentId("doc-1"),
        FileName = FileName,
        ContentType = "image/tiff",
    };
}
