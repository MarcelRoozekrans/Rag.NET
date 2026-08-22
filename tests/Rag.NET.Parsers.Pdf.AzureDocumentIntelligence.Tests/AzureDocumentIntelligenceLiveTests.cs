using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Xunit;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Env-gated live coverage, mirroring the <c>RAGNET_TESSDATA</c> precedent in
/// <c>PdfOcrFallbackTests</c>: a real call is possible but never required, so CI stays offline
/// and free while a developer with a resource can prove the cassettes still describe reality.
/// </summary>
/// <remarks>
/// <para>
/// Set <c>RAGNET_DOCINTEL_ENDPOINT</c> and <c>RAGNET_DOCINTEL_KEY</c> to run it. It bills the
/// configured resource for one page.
/// </para>
/// <para>
/// Set <c>RAGNET_DOCINTEL_CAPTURE</c> to a directory as well and every response body the run
/// receives is written there, numbered in arrival order — <c>01-202.json</c> for the analyze
/// answer, then one file per poll, the last of which carries the whole <c>analyzeResult</c>.
/// That is how a real payload gets into a cassette here: the mapping envelope stays
/// hand-written (see <see cref="ResponseCapturePolicy"/> for why a proxy recording cannot work
/// for a long-running operation), and only the body is replaced with what the service really
/// sent. Nothing of the caller's is in it — the document analysed is this repository's own
/// <c>sample-scanned.pdf</c>.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceLiveTests
{
    [Fact]
    public async Task RecognizeAsync_RealService_ReadsTheScannedFixture()
    {
        var endpoint = Environment.GetEnvironmentVariable("RAGNET_DOCINTEL_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("RAGNET_DOCINTEL_KEY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key),
            "Set RAGNET_DOCINTEL_ENDPOINT and RAGNET_DOCINTEL_KEY to run the live Azure Document Intelligence test (it bills one page).");

        var ledger = new FakeCostLedger();
        var sut = new AzureDocumentIntelligenceOcrEngine(
            new Uri(endpoint!),
            new AzureKeyCredential(key!),
            new AzureDocumentIntelligenceOcrOptions(),
            ledger,
            logger: null,
            CaptureClientOptions());

        await using var resource = OpenScannedFixture();
        using var pdf = new MemoryStream();
        await resource.CopyToAsync(pdf, TestContext.Current.CancellationToken);
        pdf.Position = 0;

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BilledPages);
        Assert.Contains("OCR", result.PageText[1], StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(1, entry.Pages);
    }

    /// <summary>
    /// Client options that capture every response body when <c>RAGNET_DOCINTEL_CAPTURE</c> names
    /// a directory, and are the SDK's defaults when it does not.
    /// </summary>
    /// <returns>The options to construct the engine with.</returns>
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

    private static Stream OpenScannedFixture()
    {
        const string ResourceName =
            "Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests.Resources.sample-scanned.pdf";
        return typeof(AzureDocumentIntelligenceLiveTests).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
    }
}
