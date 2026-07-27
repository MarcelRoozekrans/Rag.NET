using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Parsers.Pdf.Ocr;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

/// <summary>
/// An <see cref="IDocumentOcrEngine"/> backed by Azure AI Document Intelligence: the whole PDF
/// is submitted in one request, the service rasterizes server-side, and every page comes back
/// from a single long-running operation.
/// </summary>
/// <remarks>
/// <b>Billing.</b> The service prices per page of the <i>submitted</i> document, so
/// <c>DocumentOcrResult.BilledPages</c> is the analyzed page count and not the number of pages
/// that happened to contain text. Spend is recorded to <see cref="ICostLedger"/> when one is
/// registered; when none is, recording is a silent no-op rather than an error.
/// <para>
/// <b>Stated limitation.</b> Recorded OCR spend reaches <c>UseCostBudgeting</c>'s spend window
/// but emits no <c>ragnet.llm.cost</c> / <c>ragnet.llm.tokens</c> telemetry — see
/// <see cref="AzureDocumentIntelligenceOcrOptions"/> for why and what it means for dashboards.
/// </para>
/// <para>
/// <b>Failures throw.</b> Per the seam's contract the parser catches, logs and degrades to its
/// own extraction losslessly; swallowing here would hide throttling and auth problems from the
/// one component able to report them. Cancellation propagates.
/// </para>
/// <para>
/// <b>Memory: budget for roughly twice the document.</b> The SDK's
/// <see cref="AnalyzeDocumentOptions"/> accepts <see cref="BinaryData"/> rather than a
/// <see cref="Stream"/>, so the PDF is copied into a second in-memory buffer for the duration
/// of the call — on top of the full-document buffer the parser already holds to feed both
/// PdfPig and this engine. Peak resident bytes are therefore about 2× the document size per
/// concurrent OCR call, large ones on the large object heap. That is forced by the SDK's
/// surface, not a choice made here, but it compounds the parser's own memory profile and is
/// worth sizing the host against.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceOcrEngine : IDocumentOcrEngine
{
    private readonly DocumentIntelligenceClient _client;
    private readonly AzureDocumentIntelligenceOcrOptions _options;
    private readonly ICostLedger? _costLedger;
    private readonly ILogger<AzureDocumentIntelligenceOcrEngine>? _logger;

    /// <summary>Creates an engine authenticating with an API key.</summary>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The resource's API key.</param>
    /// <param name="options">Model, pricing and polling settings. Defaults when omitted.</param>
    /// <param name="costLedger">Ledger to record page spend to. Omit for no recording.</param>
    /// <param name="logger">Logger. Omit for no logging.</param>
    public AzureDocumentIntelligenceOcrEngine(
        Uri endpoint,
        AzureKeyCredential credential,
        AzureDocumentIntelligenceOcrOptions? options = null,
        ICostLedger? costLedger = null,
        ILogger<AzureDocumentIntelligenceOcrEngine>? logger = null)
        : this(endpoint, credential, options, costLedger, logger, clientOptions: null)
    {
    }

    /// <inheritdoc cref="AzureDocumentIntelligenceOcrEngine(Uri, AzureKeyCredential, AzureDocumentIntelligenceOcrOptions, ICostLedger, ILogger{AzureDocumentIntelligenceOcrEngine})"/>
    /// <param name="clientOptions">
    /// Transport-level client options. Deliberately absent from the builder extensions: it
    /// exists so tests can inject a transport or neuter retries, not as a supported
    /// configuration surface.
    /// </param>
    public AzureDocumentIntelligenceOcrEngine(
        Uri endpoint,
        AzureKeyCredential credential,
        AzureDocumentIntelligenceOcrOptions? options,
        ICostLedger? costLedger,
        ILogger<AzureDocumentIntelligenceOcrEngine>? logger,
        DocumentIntelligenceClientOptions? clientOptions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        _client = clientOptions is null
            ? new DocumentIntelligenceClient(endpoint, credential)
            : new DocumentIntelligenceClient(endpoint, credential, clientOptions);
        _options = Validate(options ?? new AzureDocumentIntelligenceOcrOptions(), nameof(options));
        _costLedger = costLedger;
        _logger = logger;
    }

    /// <summary>Creates an engine authenticating with OAuth / managed identity.</summary>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The token credential to authenticate with.</param>
    /// <param name="options">Model, pricing and polling settings. Defaults when omitted.</param>
    /// <param name="costLedger">Ledger to record page spend to. Omit for no recording.</param>
    /// <param name="logger">Logger. Omit for no logging.</param>
    public AzureDocumentIntelligenceOcrEngine(
        Uri endpoint,
        TokenCredential credential,
        AzureDocumentIntelligenceOcrOptions? options = null,
        ICostLedger? costLedger = null,
        ILogger<AzureDocumentIntelligenceOcrEngine>? logger = null)
        : this(endpoint, credential, options, costLedger, logger, clientOptions: null)
    {
    }

    /// <inheritdoc cref="AzureDocumentIntelligenceOcrEngine(Uri, TokenCredential, AzureDocumentIntelligenceOcrOptions, ICostLedger, ILogger{AzureDocumentIntelligenceOcrEngine})"/>
    /// <param name="clientOptions">
    /// Transport-level client options. Deliberately absent from the builder extensions: it
    /// exists so tests can inject a transport or neuter retries, not as a supported
    /// configuration surface.
    /// </param>
    public AzureDocumentIntelligenceOcrEngine(
        Uri endpoint,
        TokenCredential credential,
        AzureDocumentIntelligenceOcrOptions? options,
        ICostLedger? costLedger,
        ILogger<AzureDocumentIntelligenceOcrEngine>? logger,
        DocumentIntelligenceClientOptions? clientOptions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        _client = clientOptions is null
            ? new DocumentIntelligenceClient(endpoint, credential)
            : new DocumentIntelligenceClient(endpoint, credential, clientOptions);
        _options = Validate(options ?? new AzureDocumentIntelligenceOcrOptions(), nameof(options));
        _costLedger = costLedger;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<DocumentOcrResult> RecognizeAsync(Stream pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // Reads to the end but never disposes: the parser owns this stream.
        var content = await BinaryData.FromStreamAsync(pdf, cancellationToken).ConfigureAwait(false);

        var analyzeOptions = new AnalyzeDocumentOptions(_options.ModelId, content);
        if (!string.IsNullOrWhiteSpace(_options.Locale))
        {
            analyzeOptions.Locale = _options.Locale;
        }

        // WaitUntil.Started, then an explicit wait: the parameterless completion wait uses the
        // SDK's own back-off, which nothing on DocumentIntelligenceClientOptions can influence.
        var operation = await _client
            .AnalyzeDocumentAsync(WaitUntil.Started, analyzeOptions, cancellationToken)
            .ConfigureAwait(false);
        await operation.WaitForCompletionAsync(_options.PollingInterval, cancellationToken).ConfigureAwait(false);

        var pageText = BuildPageText(operation.Value, out var billedPages);
        if (_logger is not null)
        {
            AzureDocumentIntelligenceLog.Analyzed(_logger, billedPages, _options.ModelId, pageText.Count);
        }

        await RecordCostAsync(billedPages, cancellationToken).ConfigureAwait(false);
        return new DocumentOcrResult(pageText, billedPages);
    }

    /// <summary>
    /// Projects the analyze result onto the seam's shape: text keyed by 1-based page number
    /// with empty pages omitted, and <paramref name="billedPages"/> set from the number of
    /// pages the service <i>analyzed</i>. Those two counts differ whenever a page carries no
    /// recognizable text, and conflating them would under-report spend.
    /// </summary>
    private static Dictionary<int, string> BuildPageText(AnalyzeResult result, out int billedPages)
    {
        var pages = result.Pages;
        billedPages = pages is null ? 0 : pages.Count;
        var text = new Dictionary<int, string>(billedPages);
        if (pages is null)
        {
            return text;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            builder.Clear();
            AppendPageContent(page, builder);
            if (builder.Length > 0)
            {
                text[page.PageNumber] = builder.ToString();
            }
        }

        return text;
    }

    /// <summary>
    /// Appends a page's recognized text. Lines are preferred over words because the service
    /// already grouped them into reading order; words are the fallback for models that return
    /// no line segmentation.
    /// </summary>
    private static void AppendPageContent(DocumentPage page, StringBuilder builder)
    {
        var lines = page.Lines;
        if (lines is not null && lines.Count > 0)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(lines[i].Content);
            }

            return;
        }

        var words = page.Words;
        if (words is null)
        {
            return;
        }

        for (int i = 0; i < words.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(words[i].Content);
        }
    }

    /// <summary>
    /// Records page spend, computing the cost from the configured per-page price because the
    /// ledger prices nothing itself. An OCR entry carries pages and zero tokens.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget by house convention: the OCR call already succeeded, so a ledger write
    /// failure is logged and swallowed rather than discarding a result the caller paid for.
    /// Cancellation is the one exception — it propagates, as it does everywhere else here.
    /// </remarks>
    private async Task RecordCostAsync(int billedPages, CancellationToken cancellationToken)
    {
        if (_costLedger is null || billedPages <= 0)
        {
            return;
        }

        var entry = new CostEntry
        {
            Kind = CostKind.Ocr,
            Pages = billedPages,
            Cost = billedPages * _options.PricePerPage,
        };

        try
        {
            await _costLedger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                AzureDocumentIntelligenceLog.CostLedgerWriteFailed(_logger, billedPages, exception);
            }
        }
    }

    /// <summary>
    /// Validates every configurable value and returns <paramref name="options"/> unchanged.
    /// </summary>
    /// <param name="options">The options to check.</param>
    /// <param name="paramName">
    /// The name to blame in the thrown exception — <c>options</c> when a caller constructed the
    /// engine directly, <c>configure</c> when the builder extensions did.
    /// </param>
    /// <remarks>
    /// Internal rather than private so
    /// <see cref="AzureDocumentIntelligenceBuilderExtensions"/> can run it at <i>registration</i>
    /// time. Leaving it to the constructor would defer every check but <c>ModelId</c> into the
    /// <c>UseDocumentOcrEngine</c> factory lambda, which the container does not invoke until the
    /// parser is first resolved — so a negative price would register silently and surface much
    /// later, out of a DI factory. Configuration errors stay loud and immediate.
    /// </remarks>
    internal static AzureDocumentIntelligenceOcrOptions Validate(
        AzureDocumentIntelligenceOcrOptions options, string paramName)
    {
        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new ArgumentException(
                $"{nameof(AzureDocumentIntelligenceOcrOptions)}.{nameof(AzureDocumentIntelligenceOcrOptions.ModelId)} " +
                "must be a non-empty string.",
                paramName);
        }

        if (options.PricePerPage < 0m)
        {
            throw new ArgumentOutOfRangeException(paramName, options.PricePerPage,
                $"{nameof(AzureDocumentIntelligenceOcrOptions)}.{nameof(AzureDocumentIntelligenceOcrOptions.PricePerPage)} " +
                "must not be negative.");
        }

        if (options.PollingInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, options.PollingInterval,
                $"{nameof(AzureDocumentIntelligenceOcrOptions)}.{nameof(AzureDocumentIntelligenceOcrOptions.PollingInterval)} " +
                "must not be negative.");
        }

        return options;
    }
}
