using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;
using Xunit;
using DocumentSection = Rag.NET.Models.DocumentSection;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// Env-gated live coverage of the parser, mirroring the PDF OCR engine's
/// <c>AzureDocumentIntelligenceLiveTests</c>: a real call is possible but never required, so CI
/// stays offline and free while a run with a resource proves the hand-written cassettes still
/// describe reality.
/// </summary>
/// <remarks>
/// <para>
/// Set <c>RAGNET_DOCINTEL_ENDPOINT</c> and <c>RAGNET_DOCINTEL_KEY</c> to run it. It bills the
/// configured resource for one page: the fixture is a single-frame TIFF.
/// </para>
/// <para>
/// Set <c>RAGNET_DOCINTEL_CAPTURE</c> to a directory as well and every response body the run
/// receives is written there, numbered in arrival order, through the same
/// <see cref="ResponseCapturePolicy"/> the engine's suite uses (linked, not copied). The last
/// file carries the whole <c>analyzeResult</c>; that body is what replaces a hand-written one in
/// this package's cassettes. The document analysed is this repository's own
/// <c>Resources/sample-scan.tif</c>, drawn by the script in <c>Resources/README.md</c>.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceParserLiveTests
{
    /// <summary>The word the fixture prints on its third line, chosen to be unambiguous to OCR.</summary>
    private const string FixtureKeyword = "MARIGOLD";

    [Fact]
    public async Task ParseAsync_AgainstTheRealService_ReturnsTheScansText()
    {
        var endpoint = Environment.GetEnvironmentVariable("RAGNET_DOCINTEL_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("RAGNET_DOCINTEL_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key),
            "Set RAGNET_DOCINTEL_ENDPOINT and RAGNET_DOCINTEL_KEY to run the live Azure Document Intelligence parser test (it bills one page).");

        var ledger = new FakeCostLedger();
        var options = new AzureDocumentIntelligenceParserOptions();
        var analyzer = new AzureDocumentIntelligenceAnalyzer(
            new Uri(endpoint!),
            new AzureKeyCredential(key!),
            options.Analyzer,
            ledger,
            logger: null,
            CaptureClientOptions());
        var sut = new AzureDocumentIntelligenceDocumentParser(analyzer, options);

        await using var tiff = OpenScanFixture();
        var metadata = new DocumentMetadata
        {
            DocumentId = new DocumentId("sample-scan"),
            FileName = "sample-scan.tif",
            ContentType = "image/tiff",
        };

        var sections = new List<DocumentSection>();
        await foreach (var section in sut.ParseAsync(tiff, metadata, TestContext.Current.CancellationToken))
        {
            sections.Add(section);
        }

        var page = Assert.Single(sections);
        Assert.Equal(1, page.PageNumber);
        Assert.Contains(FixtureKeyword, page.Text, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(1, entry.Pages);
    }

    /// <summary>
    /// Runs without credentials, so the fixture the live test depends on is checked in every
    /// tier: the resource name resolves, and the file is a little-endian TIFF with exactly one
    /// frame, which is what makes the live test's one-page and one-ledger-page assertions hold.
    /// </summary>
    [Fact]
    public async Task ScanFixture_IsASingleFrameTiff()
    {
        await using var resource = OpenScanFixture();
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var bytes = buffer.ToArray();

        Assert.Equal("II*\0"u8.ToArray(), bytes[..4]);
        var firstDirectory = (int)BitConverter.ToUInt32(bytes, 4);
        var entryCount = BitConverter.ToUInt16(bytes, firstDirectory);
        var nextDirectory = BitConverter.ToUInt32(bytes, firstDirectory + 2 + (12 * entryCount));
        Assert.Equal(0u, nextDirectory);
    }

    /// <summary>
    /// Client options that capture every response body when <c>RAGNET_DOCINTEL_CAPTURE</c> names
    /// a directory, and are the SDK's defaults when it does not.
    /// </summary>
    /// <returns>The options to construct the analyzer with.</returns>
    private static DocumentIntelligenceClientOptions CaptureClientOptions()
    {
        var options = new DocumentIntelligenceClientOptions();
        var directory = Environment.GetEnvironmentVariable("RAGNET_DOCINTEL_CAPTURE");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            // PerCall rather than PerRetry: one file per HTTP call the SDK makes, which is the
            // unit a cassette mapping corresponds to, instead of one per transport attempt.
            options.AddPolicy(new ResponseCapturePolicy(directory), HttpPipelinePosition.PerCall);
        }

        return options;
    }

    private static Stream OpenScanFixture()
    {
        const string ResourceName =
            "Rag.NET.Parsers.AzureDocumentIntelligence.Tests.Resources.sample-scan.tif";
        return typeof(AzureDocumentIntelligenceParserLiveTests).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
    }
}
