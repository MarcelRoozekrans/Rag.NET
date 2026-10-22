using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Rag.NET.Models;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Parsers.AzureDocumentIntelligence.Tests;

/// <summary>
/// Cassette-driven coverage of the shared analyzer against the real SDK and its real
/// long-running operation machinery: every successful call goes through the 202 +
/// <c>Operation-Location</c> handshake and one poll.
/// </summary>
/// <remarks>
/// Each scenario gets its own <c>modelId</c>, which is part of the analyze path, so one cassette
/// directory serves every test without the mappings colliding. The cassettes are hand-written
/// from the documented response shape, not recorded.
/// </remarks>
[Collection("WireMock")]
public sealed class AzureDocumentIntelligenceAnalyzerTests
{
    private const string TwoPagesModel = "two-pages";
    private const string NoPagesModel = "no-pages";
    private const string UnauthorizedModel = "unauthorized";

    /// <summary>The engine's <c>CostLedgerWriteFailed</c> event id, which the analyzer reuses.</summary>
    private const int CostLedgerWriteFailedEventId = 25359835;

    private readonly WireMockServerFixture _fixture;

    public AzureDocumentIntelligenceAnalyzerTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("AzureDocumentIntelligence");
        _fixture.Server.ResetLogEntries();
    }

    [Fact]
    public async Task AnalyzeAsync_TwoPageResult_RecordsTwoPagesAtThePricePerPage()
    {
        var ledger = new FakeCostLedger();
        var sut = CreateAnalyzer(TwoPagesModel, ledger);

        var result = await sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pages.Count);
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(CostKind.Ocr, entry.Kind);
        Assert.Equal(2, entry.Pages);
        // Priced by the analyzer from its own settings; the ledger prices nothing.
        Assert.Equal(2 * 0.0015m, entry.Cost);
        // A per-page API reports no tokens, so none are fabricated.
        Assert.Equal(0, entry.InputTokens);
        Assert.Equal(0, entry.OutputTokens);
    }

    [Fact]
    public async Task AnalyzeAsync_ResultWithoutPages_RecordsNothing()
    {
        // Zero pages is not a zero-cost entry, it is no entry: there is nothing to record.
        var ledger = new FakeCostLedger();
        var sut = CreateAnalyzer(NoPagesModel, ledger);

        var result = await sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken);

        Assert.Null(result.Pages);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task AnalyzeAsync_LedgerThrows_StillReturnsTheResult()
    {
        // The call is already paid for by the time the ledger is written. Losing its result to
        // a bookkeeping failure would be strictly worse than an under-recorded budget.
        var failure = new InvalidOperationException("ledger is offline");
        var ledger = new FakeCostLedger(failure);
        var logger = new CapturingLogger();
        var sut = CreateAnalyzer(TwoPagesModel, ledger, logger);

        var result = await sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pages.Count);
        Assert.Empty(ledger.Entries);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(CostLedgerWriteFailedEventId, warning.EventId.Id);
        Assert.Equal("cost_ledger_write_failed", warning.EventId.Name);
        Assert.Same(failure, warning.Exception);
        Assert.Equal(
            "Recording 2 OCR page(s) to the cost ledger failed; the OCR result is unaffected but " +
            "this spend is missing from the budget window",
            warning.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_LedgerThrowsOperationCanceled_Propagates()
    {
        // Cancellation is the one ledger failure that is not swallowed.
        var ledger = new FakeCostLedger(new OperationCanceledException("cancelled in the ledger"));
        var sut = CreateAnalyzer(TwoPagesModel, ledger);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnalyzeAsync_ServiceReturns401_ThrowsRequestFailedException()
    {
        var ledger = new FakeCostLedger();
        var sut = CreateAnalyzer(UnauthorizedModel, ledger);

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken));

        Assert.Equal(401, exception.Status);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void Validate_NegativePrice_Throws()
    {
        var settings = new AzureDocumentIntelligenceAnalyzerSettings { PricePerPage = -0.01m };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, "configure"));

        Assert.Equal("configure", exception.ParamName);
        Assert.Contains(
            "AzureDocumentIntelligenceAnalyzerSettings.PricePerPage must not be negative.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankModelId_Throws(string modelId)
    {
        var settings = new AzureDocumentIntelligenceAnalyzerSettings { ModelId = modelId };

        var exception = Assert.Throws<ArgumentException>(
            () => AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, "settings"));

        Assert.Equal("settings", exception.ParamName);
        Assert.Contains(
            "AzureDocumentIntelligenceAnalyzerSettings.ModelId must be a non-empty string.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NegativePollingInterval_Throws()
    {
        var settings = new AzureDocumentIntelligenceAnalyzerSettings { PollingInterval = TimeSpan.FromSeconds(-1) };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, "settings"));

        Assert.Contains(
            "AzureDocumentIntelligenceAnalyzerSettings.PollingInterval must not be negative.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_Defaults_MatchTheOcrEngine()
    {
        var settings = new AzureDocumentIntelligenceAnalyzerSettings();

        Assert.Equal("prebuilt-read", settings.ModelId);
        Assert.Equal(0.0015m, settings.PricePerPage);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.PollingInterval);
        Assert.Null(settings.Locale);
        Assert.Same(settings, AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, "settings"));
    }

    [Fact]
    public async Task Constructor_SettingsMutatedAfterwards_HaveNoEffect()
    {
        // Validation runs in the constructor, so a later change to the caller's object must not
        // reach the service: neither an unvalidated price nor a different model.
        var ledger = new FakeCostLedger();
        var settings = new AzureDocumentIntelligenceAnalyzerSettings
        {
            ModelId = TwoPagesModel,
            PollingInterval = TimeSpan.Zero,
        };
        var sut = new AzureDocumentIntelligenceAnalyzer(
            new Uri(_fixture.BaseUrl),
            new AzureKeyCredential("cassette-key"),
            settings,
            ledger,
            logger: null,
            NoWaitingClientOptions());

        settings.ModelId = UnauthorizedModel;
        settings.PricePerPage = -1m;
        settings.PollingInterval = TimeSpan.FromSeconds(-1);
        settings.Locale = "nl-NL";

        var result = await sut.AnalyzeAsync(Content(), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(2 * 0.0015m, Assert.Single(ledger.Entries).Cost);
        var submission = Assert.Single(
            _fixture.Server.LogEntries,
            e => e.RequestMessage?.Path?.EndsWith(":analyze", StringComparison.Ordinal) == true);
        Assert.EndsWith($"{TwoPagesModel}:analyze", submission.RequestMessage!.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("locale", submission.RequestMessage.Url, StringComparison.Ordinal);
    }

    private AzureDocumentIntelligenceAnalyzer CreateAnalyzer(
        string modelId,
        FakeCostLedger? costLedger = null,
        ILogger? logger = null) =>
        new(
            new Uri(_fixture.BaseUrl),
            new AzureKeyCredential("cassette-key"),
            new AzureDocumentIntelligenceAnalyzerSettings
            {
                ModelId = modelId,
                PollingInterval = TimeSpan.Zero,
            },
            costLedger,
            logger,
            NoWaitingClientOptions());

    /// <summary>
    /// Removes Azure.Core's retry delay and its 100-second network timeout, keeping the retries
    /// so a transient loopback reset on a cold run stays recoverable. Polling delay is neutered
    /// separately, through <see cref="AzureDocumentIntelligenceAnalyzerSettings.PollingInterval"/>.
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

    private static BinaryData Content() =>
        BinaryData.FromBytes(new byte[] { 0x49, 0x49, 0x2A, 0x00 });
}
