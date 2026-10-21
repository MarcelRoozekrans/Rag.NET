using System.Text.Json;
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
        // The cassette lists paragraphs out of page order (3, 1, 2, 1), so this pins the sort
        // and the grouping of a page's non-adjacent paragraphs, not just the payload's order.
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
        // words would flatten every paragraph into one run; paragraphs keep the structure. Both
        // paragraphs' spans fall inside page 1's span, so they share page 1's section.
        var sections = await ParseAsync("docx-paragraphs");

        var section = Assert.Single(sections);
        Assert.Equal(
            "The quarterly report covers revenue, costs and the outlook for next year.\n\n" +
            "Revenue grew by twelve percent compared with the same quarter last year.",
            section.Text);
        Assert.Equal(1, section.PageNumber);
        Assert.Equal(0, section.SectionIndex);

        // The words fallback would join both sentences with a single space into one run.
        Assert.DoesNotContain("next year. Revenue grew", section.Text, StringComparison.Ordinal);
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
    public async Task ParseAsync_NoParagraphsPagesOutOfOrder_EmitsLinesInPageOrder()
    {
        var sections = await ParseAsync("lines-out-of-order");

        Assert.Equal(["ONE", "TWO", "THREE"], sections.Select(s => s.Text), StringComparer.Ordinal);
        Assert.Equal([1, 2, 3], sections.Select(s => s.PageNumber));
        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task ParseAsync_OfficeParagraphsWithoutRegions_GroupByThePageSpanHoldingThem()
    {
        // Office input: no bounding regions, but each page lists the content range it holds.
        // Each paragraph lands on the page whose span contains its first span's offset, and a
        // heading heads the page it opens and the pages after it.
        var sections = await ParseAsync("office-headings");

        Assert.Equal(2, sections.Count);

        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal("Employee Handbook\n\nWelcome to the company.", sections[0].Text);
        Assert.Equal("Employee Handbook", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);

        Assert.Equal(2, sections[1].PageNumber);
        Assert.Equal("Leave\n\nYou get twenty-five days.", sections[1].Text);
        Assert.Equal("Leave", sections[1].Heading);
        Assert.Equal(2, sections[1].HeadingLevel);

        Assert.Equal([0, 1], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task ParseAsync_ParagraphOutsideEveryPageSpan_FollowsThePagesWithoutAPage()
    {
        // Page 1 holds only the title and page 2 starts at "Leave", so "Welcome" sits in no
        // page's range. It stays page-less and comes after the paged sections, carrying the
        // heading in effect where it occurs in the document.
        var sections = await ParseAsync("partial-page-spans");

        Assert.Equal(3, sections.Count);

        Assert.Equal(1, sections[0].PageNumber);
        Assert.Equal("Employee Handbook", sections[0].Text);
        Assert.Equal("Employee Handbook", sections[0].Heading);

        Assert.Equal(2, sections[1].PageNumber);
        Assert.Equal("Leave\n\nYou get twenty-five days.", sections[1].Text);
        Assert.Equal("Leave", sections[1].Heading);

        Assert.Null(sections[2].PageNumber);
        Assert.Equal("Welcome to the company.", sections[2].Text);
        Assert.Equal("Employee Handbook", sections[2].Heading);
        Assert.Equal(1, sections[2].HeadingLevel);

        Assert.Equal([0, 1, 2], sections.Select(s => s.SectionIndex));
    }

    [Fact]
    public async Task ParseAsync_PagelessHeadings_HeadTheirOwnSectionAndTheOnesAfter()
    {
        // The genuine fallback: the result's page carries no spans, so no paragraph can be
        // placed on a page and each becomes its own page-less section, in document order.
        var sections = await ParseAsync("pageless-headings");

        Assert.Equal(4, sections.Count);
        Assert.All(sections, s => Assert.Null(s.PageNumber));

        Assert.Equal("Employee Handbook", sections[0].Text);
        Assert.Equal("Employee Handbook", sections[0].Heading);
        Assert.Equal(1, sections[0].HeadingLevel);

        Assert.Equal("Welcome to the company.", sections[1].Text);
        Assert.Equal("Employee Handbook", sections[1].Heading);
        Assert.Equal(1, sections[1].HeadingLevel);

        Assert.Equal("Leave", sections[2].Text);
        Assert.Equal("Leave", sections[2].Heading);
        Assert.Equal(2, sections[2].HeadingLevel);

        Assert.Equal("You get twenty-five days.", sections[3].Text);
        Assert.Equal("Leave", sections[3].Heading);
        Assert.Equal(2, sections[3].HeadingLevel);
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
        Assert.Equal(
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            AzureDocumentIntelligenceFormats.Word.ContentTypes());
        Assert.Equal(
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
            AzureDocumentIntelligenceFormats.Excel.ContentTypes());
        Assert.Equal(
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"],
            AzureDocumentIntelligenceFormats.PowerPoint.ContentTypes());
        Assert.Equal(3, AzureDocumentIntelligenceFormats.Office.ContentTypes().Count);
        Assert.Empty(AzureDocumentIntelligenceFormats.None.ContentTypes());
    }

    [Fact]
    public void Options_Defaults_AreTiffAndHeifWithA500MegabyteCap()
    {
        var options = new AzureDocumentIntelligenceParserOptions();

        Assert.Equal(AzureDocumentIntelligenceFormats.Tiff | AzureDocumentIntelligenceFormats.Heif, options.Formats);
        // Decimal 500 MB, as Microsoft documents the S0 limit: a binary 500 MiB cap would let
        // through files between the two that Azure then rejects after the upload.
        Assert.Equal(500_000_000L, options.MaxBytes);
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
    public async Task ParseAsync_CancelledWhilePolling_PropagatesOperationCanceledUnwrapped()
    {
        // The cassette delays the terminal poll by ten seconds; the token fires mid-wait, inside
        // the analyzer, which is the path the pre-cancelled test above never reaches. It fires
        // only once WireMock has logged the analyze POST: a fixed timer could fire before the
        // submission on a loaded runner and pass for the wrong reason.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sut = CreateParser("slow-poll");
        using var stream = Content();

        var parse = Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, Metadata(), cts.Token))
            {
            }
        }).AsTask();
        await WaitForAnalyzeSubmissionAsync("slow-poll", parse);
        await cts.CancelAsync();
        var exception = await parse;

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<AzureDocumentIntelligenceException>(exception);
        // It got as far as the analyze submission, so cancellation came from inside the analyzer.
        Assert.Contains(
            _fixture.Server.LogEntries,
            e => e.RequestMessage?.Path?.EndsWith("slow-poll:analyze", StringComparison.Ordinal) == true);
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

    [Fact]
    public async Task ParseAsync_NonSeekableStreamOverMaxBytes_ThrowsBeforeAnyRequest()
    {
        // A forward-only stream has no Length to check up front, so only the bounded read can
        // catch it: it must stop at MaxBytes + 1 bytes, not buffer the whole document first.
        var sut = CreateParser("tiff-three-frames", options: new AzureDocumentIntelligenceParserOptions { MaxBytes = 16 });
        using var stream = new NonSeekableStream(new byte[1024]);

        var exception = await Assert.ThrowsAsync<AzureDocumentIntelligenceException>(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(FileName, exception.FileName);
        Assert.Contains(FileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("16", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Azure's limit", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Equal(17, stream.BytesRead);
        Assert.Empty(_fixture.Server.LogEntries);
    }

    [Fact]
    public async Task ParseAsync_SeekableStreamOverMaxBytes_ThrowsWithoutReadingOrSending()
    {
        var sut = CreateParser("tiff-three-frames", options: new AzureDocumentIntelligenceParserOptions { MaxBytes = 16 });
        using var stream = new MemoryStream(new byte[17]);

        var exception = await Assert.ThrowsAsync<AzureDocumentIntelligenceException>(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Contains("Azure's limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.Position);
        Assert.Empty(_fixture.Server.LogEntries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParseAsync_EmptyDocument_ThrowsBeforeAnyRequest(bool seekable)
    {
        // The service answers an empty document with a 400; refusing it locally saves the round
        // trip and says what is actually wrong.
        var sut = CreateParser("tiff-three-frames");
        using Stream stream = seekable ? new MemoryStream([]) : new NonSeekableStream([]);

        var exception = await Assert.ThrowsAsync<AzureDocumentIntelligenceException>(async () =>
        {
            await foreach (var _ in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(FileName, exception.FileName);
        Assert.Contains(FileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.Empty(_fixture.Server.LogEntries);
    }

    [Fact]
    public async Task ParseAsync_NonSeekableStreamExactlyAtMaxBytes_IsSentWhole()
    {
        var sut = CreateParser("tiff-three-frames", options: new AzureDocumentIntelligenceParserOptions { MaxBytes = 16 });
        using var stream = new NonSeekableStream(new byte[16]);
        var sections = new List<DocumentSection>();

        await foreach (var section in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        Assert.Equal(3, sections.Count);
        var submission = Assert.Single(
            _fixture.Server.LogEntries,
            e => e.RequestMessage?.Path?.EndsWith("tiff-three-frames:analyze", StringComparison.Ordinal) == true);
        // The SDK posts the document as base64 inside a JSON body.
        using var body = JsonDocument.Parse(submission.RequestMessage!.BodyAsBytes);
        Assert.Equal(16, body.RootElement.GetProperty("base64Source").GetBytesFromBase64().Length);
    }

    [Fact]
    public async Task ParseAsync_SeekableStreamPartlyRead_CapsOnlyTheRemainder()
    {
        // The parser sends from the current position, so the cap applies to what is left.
        var sut = CreateParser("tiff-three-frames", options: new AzureDocumentIntelligenceParserOptions { MaxBytes = 16 });
        using var stream = new MemoryStream(new byte[20]) { Position = 4 };
        var sections = new List<DocumentSection>();

        await foreach (var section in sut.ParseAsync(stream, Metadata(), TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        Assert.Equal(3, sections.Count);
    }

    [Fact]
    public void Constructor_MaxBytesBeyondTheLargestBuffer_Throws()
    {
        // The document is buffered in one array, so a larger cap could never be honoured: it would
        // fail mid-copy with an IOException after buffering 2 GiB instead of failing here.
        var options = new AzureDocumentIntelligenceParserOptions { MaxBytes = Array.MaxLength + 1L };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateParser("unused", options: options));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains(nameof(AzureDocumentIntelligenceParserOptions.MaxBytes), exception.Message, StringComparison.Ordinal);
        CreateParser("unused", options: new AzureDocumentIntelligenceParserOptions { MaxBytes = Array.MaxLength });
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Constructor_NonPositiveMaxBytes_Throws(long maxBytes)
    {
        var options = new AzureDocumentIntelligenceParserOptions { MaxBytes = maxBytes };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => CreateParser("unused", options: options));

        Assert.Equal("options", exception.ParamName);
    }

    /// <summary>
    /// Polls WireMock's log until the analyze POST for <paramref name="modelId"/> appears, or until
    /// <paramref name="parse"/> ends first. Fails after five seconds rather than hanging, which is
    /// well inside the cassette's ten-second poll delay.
    /// </summary>
    private async Task WaitForAnalyzeSubmissionAsync(string modelId, Task parse)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!IsAnalyzeSubmission(modelId))
        {
            if (parse.IsCompleted || DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The analyze POST for '{modelId}' never reached WireMock, so cancelling now would not test polling.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    private bool IsAnalyzeSubmission(string modelId) =>
        _fixture.Server.LogEntries.Any(e =>
            e.RequestMessage?.Path?.EndsWith($"{modelId}:analyze", StringComparison.Ordinal) == true);

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
