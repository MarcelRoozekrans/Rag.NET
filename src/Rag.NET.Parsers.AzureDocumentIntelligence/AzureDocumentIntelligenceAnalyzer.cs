using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Submits one document to Azure AI Document Intelligence, waits for the long-running analyze
/// operation to finish and records the analyzed pages to <see cref="ICostLedger"/>. Shared by
/// this package's parser and the PDF OCR engine in
/// <c>Rag.NET.Parsers.Pdf.AzureDocumentIntelligence</c>, so client construction, polling and
/// cost recording exist once.
/// </summary>
/// <remarks>
/// <b>Billing.</b> The service prices per page of the <i>submitted</i> document, so the cost
/// entry carries the analyzed page count, not the number of pages that happened to contain
/// text. When no ledger is supplied, recording is a silent no-op rather than an error.
/// <para>
/// <b>Failures throw.</b> A <see cref="RequestFailedException"/> from the service and
/// cancellation propagate unchanged; deciding whether to degrade belongs to the caller. A
/// cost-ledger write failure is the one exception: it is logged and swallowed, because the
/// call has already succeeded and been paid for.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceAnalyzer
{
    private readonly DocumentIntelligenceClient _client;
    private readonly AzureDocumentIntelligenceAnalyzerSettings _settings;
    private readonly ICostLedger? _costLedger;
    private readonly ILogger? _logger;

    /// <summary>Creates an analyzer authenticating with an API key.</summary>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The resource's API key.</param>
    /// <param name="settings">Model, pricing and polling settings. Validated here.</param>
    /// <param name="costLedger">Ledger to record page spend to, or <see langword="null"/> for no recording.</param>
    /// <param name="logger">
    /// Logger, or <see langword="null"/> for no logging. Non-generic on purpose: the PDF OCR
    /// engine passes its own <c>ILogger&lt;AzureDocumentIntelligenceOcrEngine&gt;</c>, so the log
    /// category its users filter on does not change.
    /// </param>
    /// <param name="clientOptions">
    /// Transport-level client options, or <see langword="null"/> for the SDK's defaults. Exists so
    /// tests can inject a transport or neuter retries, not as a supported configuration surface.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/>, <paramref name="credential"/> or <paramref name="settings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="settings"/> fails validation.</exception>
    public AzureDocumentIntelligenceAnalyzer(
        Uri endpoint,
        AzureKeyCredential credential,
        AzureDocumentIntelligenceAnalyzerSettings settings,
        ICostLedger? costLedger,
        ILogger? logger,
        DocumentIntelligenceClientOptions? clientOptions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        _client = clientOptions is null
            ? new DocumentIntelligenceClient(endpoint, credential)
            : new DocumentIntelligenceClient(endpoint, credential, clientOptions);
        _settings = AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, nameof(settings));
        _costLedger = costLedger;
        _logger = logger;
    }

    /// <summary>Creates an analyzer authenticating with OAuth / managed identity.</summary>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The token credential to authenticate with.</param>
    /// <param name="settings">Model, pricing and polling settings. Validated here.</param>
    /// <param name="costLedger">Ledger to record page spend to, or <see langword="null"/> for no recording.</param>
    /// <param name="logger">
    /// Logger, or <see langword="null"/> for no logging. Non-generic on purpose: the PDF OCR
    /// engine passes its own <c>ILogger&lt;AzureDocumentIntelligenceOcrEngine&gt;</c>, so the log
    /// category its users filter on does not change.
    /// </param>
    /// <param name="clientOptions">
    /// Transport-level client options, or <see langword="null"/> for the SDK's defaults. Exists so
    /// tests can inject a transport or neuter retries, not as a supported configuration surface.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="endpoint"/>, <paramref name="credential"/> or <paramref name="settings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="settings"/> fails validation.</exception>
    public AzureDocumentIntelligenceAnalyzer(
        Uri endpoint,
        TokenCredential credential,
        AzureDocumentIntelligenceAnalyzerSettings settings,
        ICostLedger? costLedger,
        ILogger? logger,
        DocumentIntelligenceClientOptions? clientOptions)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        _client = clientOptions is null
            ? new DocumentIntelligenceClient(endpoint, credential)
            : new DocumentIntelligenceClient(endpoint, credential, clientOptions);
        _settings = AzureDocumentIntelligenceAnalyzerSettings.Validate(settings, nameof(settings));
        _costLedger = costLedger;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes <paramref name="content"/> with the configured model and returns the service's
    /// result once the operation completes. Page spend is recorded before returning.
    /// </summary>
    /// <param name="content">The document bytes.</param>
    /// <param name="cancellationToken">Cancels the submission, the polling and the ledger write.</param>
    /// <returns>The completed analyze result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="RequestFailedException">The service rejected the request or the operation failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<AnalyzeResult> AnalyzeAsync(BinaryData content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var analyzeOptions = new AnalyzeDocumentOptions(_settings.ModelId, content);
        if (!string.IsNullOrWhiteSpace(_settings.Locale))
        {
            analyzeOptions.Locale = _settings.Locale;
        }

        // WaitUntil.Started, then an explicit wait: the parameterless completion wait uses the
        // SDK's own back-off, which nothing on DocumentIntelligenceClientOptions can influence.
        var operation = await _client
            .AnalyzeDocumentAsync(WaitUntil.Started, analyzeOptions, cancellationToken)
            .ConfigureAwait(false);
        await operation.WaitForCompletionAsync(_settings.PollingInterval, cancellationToken).ConfigureAwait(false);

        var result = operation.Value;
        await RecordCostAsync(result.Pages?.Count ?? 0, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Records page spend, computing the cost from the configured per-page price because the
    /// ledger prices nothing itself. An OCR entry carries pages and zero tokens.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget by house convention: the analyze call already succeeded, so a ledger write
    /// failure is logged and swallowed rather than discarding a result the caller paid for.
    /// Cancellation is the one exception — it propagates, as it does everywhere else here.
    /// </remarks>
    private async Task RecordCostAsync(int pages, CancellationToken cancellationToken)
    {
        if (_costLedger is null || pages <= 0)
        {
            return;
        }

        var entry = new CostEntry
        {
            Kind = CostKind.Ocr,
            Pages = pages,
            Cost = pages * _settings.PricePerPage,
        };

        try
        {
            await _costLedger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                AzureDocumentIntelligenceLog.CostLedgerWriteFailed(_logger, pages, exception);
            }
        }
    }
}
