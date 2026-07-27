using Azure;
using Xunit;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Env-gated live coverage, mirroring the <c>RAGNET_TESSDATA</c> precedent in
/// <c>PdfOcrFallbackTests</c>: a real call is possible but never required, so CI stays offline
/// and free while a developer with a resource can prove the cassettes still describe reality.
/// </summary>
/// <remarks>
/// Set <c>RAGNET_DOCINTEL_ENDPOINT</c> and <c>RAGNET_DOCINTEL_KEY</c> to run it. It bills the
/// configured resource for one page.
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
            ledger);

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

    private static Stream OpenScannedFixture()
    {
        const string ResourceName =
            "Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests.Resources.sample-scanned.pdf";
        return typeof(AzureDocumentIntelligenceLiveTests).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");
    }
}
