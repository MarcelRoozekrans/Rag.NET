using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Pipeline;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

/// <summary>
/// Cassette-driven coverage of the engine against the real SDK and its real long-running
/// operation machinery: every test goes through the 202 + <c>Operation-Location</c> handshake
/// and at least one poll, because that handshake is where a document-level OCR client is most
/// likely to be wrong.
/// </summary>
/// <remarks>
/// Each scenario gets its own <c>modelId</c> rather than its own cassette directory. The model
/// id is part of the analyze path, so one cassette set serves every test without the mappings
/// colliding. Cassettes are still reloaded per test — xUnit constructs the class once per test
/// method and <c>LoadCassettes</c> resets and re-reads the mappings each time — which is
/// harmless here and is what keeps the request log scoped to a single test.
/// </remarks>
[Collection("WireMock")]
public sealed class AzureDocumentIntelligenceOcrEngineTests
{
    private const string TwoPagesModel = "prebuilt-read";
    private const string SparsePagesModel = "sparse-pages";
    private const string WordsOnlyModel = "words-only";
    private const string NoPagesModel = "no-pages";
    private const string FailingModel = "failing";
    private const string RunningModel = "running";

    private readonly WireMockServerFixture _fixture;

    public AzureDocumentIntelligenceOcrEngineTests(WireMockServerFixture fixture)
    {
        _fixture = fixture;
        _fixture.LoadCassettes("AzureDocumentIntelligence");
        _fixture.Server.ResetLogEntries();
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsPerPageText()
    {
        var sut = CreateEngine(TwoPagesModel);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        // 1-based keys, matching PdfPig's Page.Number, so the parser needs no translation.
        Assert.Equal([1, 2], result.PageText.Keys.Order());
        Assert.Equal("PAGE ONE TEXT", result.PageText[1]);
        Assert.Equal("PAGE TWO TEXT\nSECOND LINE", result.PageText[2]);
    }

    [Fact]
    public async Task RecognizeAsync_BilledPages_IsTheDocumentPageCount_NotTheRecognisedPageCount()
    {
        // The cassette analyzes three pages and recognizes text on exactly one. Azure bills
        // per submitted page, so reporting 1 here would under-report spend by two thirds —
        // and would do it silently, which is why this assertion exists at all.
        var sut = CreateEngine(SparsePagesModel);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.BilledPages);
        var only = Assert.Single(result.PageText);
        Assert.Equal(2, only.Key);
        Assert.Equal("ONLY THIS PAGE HAS TEXT", only.Value);
    }

    [Fact]
    public async Task RecognizeAsync_PageWithWordsButNoLines_FallsBackToWords()
    {
        // Lines are preferred because the service already put them in reading order, but a
        // model that returns only word segmentation must still produce text rather than
        // silently dropping the page.
        var sut = CreateEngine(WordsOnlyModel);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.BilledPages);
        Assert.Equal("WORD ONE TWO", result.PageText[1]);
    }

    [Fact]
    public async Task RecognizeAsync_ResultWithoutPages_BillsNothingAndReturnsNoText()
    {
        // A terminal result whose analyzeResult omits "pages" entirely. The SDK's generated
        // deserializer leaves AnalyzeResult.Pages genuinely null in that case rather than
        // substituting an empty list, so the engine's null guard is a live path and not
        // defensive decoration. Nothing was analyzed, so nothing is billed and nothing is
        // claimed — the parser keeps its own extraction for every page.
        var ledger = new FakeCostLedger();
        var sut = CreateEngine(NoPagesModel, ledger);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.BilledPages);
        Assert.Empty(result.PageText);
        // Zero pages is not a zero-cost entry, it is no entry: there is nothing to record.
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task RecognizeAsync_Locale_IsSentWhenSetAndOmittedWhenBlank()
    {
        // Locale is a hint the service can use to pick a recognizer. Sending a blank one would
        // be worse than sending none, so whitespace must be dropped rather than forwarded.
        var withLocale = CreateEngine(TwoPagesModel, configure: o => o.Locale = "en-US");
        using var firstPdf = CreatePdfStream();
        await withLocale.RecognizeAsync(firstPdf, TestContext.Current.CancellationToken);
        Assert.Contains("locale=en-US", LastAnalyzeRequestUrl(), StringComparison.Ordinal);

        _fixture.Server.ResetLogEntries();

        var blankLocale = CreateEngine(TwoPagesModel, configure: o => o.Locale = "   ");
        using var secondPdf = CreatePdfStream();
        await blankLocale.RecognizeAsync(secondPdf, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("locale=", LastAnalyzeRequestUrl(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecognizeAsync_RecordsCostWithPagesAndZeroTokens()
    {
        var ledger = new FakeCostLedger();
        var sut = CreateEngine(SparsePagesModel, ledger, o => o.PricePerPage = 0.002m);
        using var pdf = CreatePdfStream();

        await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(CostKind.Ocr, entry.Kind);
        Assert.Equal(3, entry.Pages);
        // Zero tokens, never a fabricated count: a per-page API reports no tokens at all.
        Assert.Equal(0, entry.InputTokens);
        Assert.Equal(0, entry.OutputTokens);
        // Priced by the engine from its own configuration; the ledger prices nothing.
        Assert.Equal(0.006m, entry.Cost);
    }

    [Fact]
    public async Task RecognizeAsync_NoLedgerRegistered_StillSucceeds()
    {
        // Recording is a no-op without a ledger, not an error: cost tracking is opt-in.
        var sut = CreateEngine(TwoPagesModel, costLedger: null);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.BilledPages);
        Assert.Equal(2, result.PageText.Count);
    }

    [Fact]
    public async Task RecognizeAsync_LedgerWriteFails_OcrStillSucceeds()
    {
        // The call is already paid for by the time the ledger is written. Losing its result
        // to a bookkeeping failure would be strictly worse than an under-recorded budget.
        var ledger = new FakeCostLedger(new InvalidOperationException("ledger is offline"));
        var sut = CreateEngine(TwoPagesModel, ledger);
        using var pdf = CreatePdfStream();

        var result = await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.BilledPages);
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public async Task RecognizeAsync_Cancellation_StopsPolling()
    {
        // The cassette's operation never reaches a terminal state, so the only way out is the
        // caller's token. Cancellation is triggered by the first poll response actually coming
        // back — observed through a pipeline policy injected via the client-options test seam —
        // rather than by a timer, so the test proves polling was under way and stays
        // deterministic without sleeping.
        var firstPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = new PollCounter();
        var sut = CreateEngine(
            RunningModel,
            configure: o => o.PollingInterval = TimeSpan.FromMilliseconds(20),
            policy: new PollSignallingPolicy(() =>
            {
                polls.Increment();
                firstPoll.TrySetResult();
            }));
        using var pdf = CreatePdfStream();
        using var cts = new CancellationTokenSource();

        var recognize = sut.RecognizeAsync(pdf, cts.Token).AsTask();
        var reached = await Task.WhenAny(
            firstPoll.Task,
            recognize).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (ReferenceEquals(reached, recognize))
        {
            // Surfaces the real failure instead of timing out on a poll that never happened.
            await recognize;
        }

        var pollsAtCancellation = polls.Value;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recognize.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // Not merely "the await unwound": the poll loop stopped. One extra poll is allowed
        // because a request may already have been in flight when the token was cancelled;
        // anything beyond that would mean polling outlived the caller's cancellation.
        Assert.InRange(polls.Value, pollsAtCancellation, pollsAtCancellation + 1);
    }

    [Fact]
    public async Task RecognizeAsync_ServiceError_Throws()
    {
        // Throwing is the contract: the parser catches, logs and falls back to PdfPig's own
        // extraction losslessly. Swallowing here would hide throttling and auth failures from
        // the one component able to report them.
        var sut = CreateEngine(FailingModel);
        using var pdf = CreatePdfStream();

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            async () => await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken));

        Assert.Equal("InvalidRequest", exception.ErrorCode);
    }

    [Fact]
    public async Task RecognizeAsync_DoesNotDisposeTheCallersStream()
    {
        // The parser owns the stream and reuses it; an engine disposing it would break the
        // caller in a way nothing else observes.
        var sut = CreateEngine(TwoPagesModel);
        var pdf = new DisposeTrackingStream([0x25, 0x50, 0x44, 0x46]);

        await sut.RecognizeAsync(pdf, TestContext.Current.CancellationToken);

        Assert.False(pdf.WasDisposed);
        Assert.True(pdf.CanRead);
        pdf.Dispose();
    }

    private AzureDocumentIntelligenceOcrEngine CreateEngine(
        string modelId,
        ICostLedger? costLedger = null,
        Action<AzureDocumentIntelligenceOcrOptions>? configure = null,
        HttpPipelinePolicy? policy = null)
    {
        var options = new AzureDocumentIntelligenceOcrOptions
        {
            ModelId = modelId,
            PollingInterval = TimeSpan.Zero,
        };
        configure?.Invoke(options);

        return new AzureDocumentIntelligenceOcrEngine(
            new Uri(_fixture.BaseUrl),
            new AzureKeyCredential("cassette-key"),
            options,
            costLedger,
            logger: null,
            NoWaitingClientOptions(policy));
    }

    /// <summary>
    /// The transport half of "no waiting". What costs wall-clock time is Azure.Core's retry
    /// <i>delay</i> — exponential by default, growing from a second — and its 100-second
    /// network timeout, so both are removed. The retries themselves are kept: dropping them
    /// made the suite flaky, because a single transient loopback connection reset on a cold
    /// run became unrecoverable. With <see cref="RetryMode.Fixed"/> and a zero delay the
    /// retries cost nothing when they are not needed and save the run when they are.
    /// <para>
    /// Polling delay is neutered separately, through
    /// <see cref="AzureDocumentIntelligenceOcrOptions.PollingInterval"/>: the client options
    /// have no say over long-running-operation polling.
    /// </para>
    /// </summary>
    private static DocumentIntelligenceClientOptions NoWaitingClientOptions(HttpPipelinePolicy? policy)
    {
        var clientOptions = new DocumentIntelligenceClientOptions();
        clientOptions.Retry.Mode = RetryMode.Fixed;
        clientOptions.Retry.MaxRetries = 3;
        clientOptions.Retry.Delay = TimeSpan.Zero;
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
        if (policy is not null)
        {
            clientOptions.AddPolicy(policy, HttpPipelinePosition.PerCall);
        }

        return clientOptions;
    }

    /// <summary>The URL of the most recent analyze request WireMock saw.</summary>
    private string LastAnalyzeRequestUrl()
    {
        string? url = null;
        foreach (var entry in _fixture.Server.LogEntries)
        {
            var request = entry.RequestMessage;
            Assert.NotNull(request);
            if (request.Url.Contains(":analyze", StringComparison.Ordinal))
            {
                url = request.Url;
            }
        }

        return url ?? throw new InvalidOperationException("No analyze request was recorded.");
    }

    /// <summary>Thread-safe counter: polls are observed on pipeline threads, read on the test's.</summary>
    private sealed class PollCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);
    }

    private static MemoryStream CreatePdfStream() =>
        new([0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37]);

    /// <summary>
    /// Signals once a poll response has actually come back. Injecting it is the whole point of
    /// the engine's <c>DocumentIntelligenceClientOptions</c> constructor parameter: the builder
    /// extensions deliberately do not expose it, so only tests can reach it.
    /// </summary>
    private sealed class PollSignallingPolicy(Action onPollCompleted) : HttpPipelineSynchronousPolicy
    {
        public override void OnReceivedResponse(HttpMessage message)
        {
            if (message.Request.Method == RequestMethod.Get)
            {
                onPollCompleted();
            }
        }
    }
}
