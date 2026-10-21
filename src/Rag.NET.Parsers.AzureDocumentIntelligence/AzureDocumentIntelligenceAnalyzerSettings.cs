namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Model, pricing and polling settings for <see cref="AzureDocumentIntelligenceAnalyzer"/>.
/// </summary>
/// <remarks>
/// The defaults are the PDF OCR engine's, because that engine delegates to the same analyzer.
/// <para>
/// <b>Telemetry asymmetry, stated deliberately.</b> Spend is written to <c>ICostLedger</c>, so
/// it counts toward the window <c>UseCostBudgeting</c> enforces for chat and embedding calls.
/// It does <b>not</b> emit the <c>ragnet.llm.cost</c> / <c>ragnet.llm.tokens</c> meters, because
/// the type that publishes them is internal to <c>Rag.NET</c>. Dashboards built on those meters
/// under-report total spend by exactly this portion; query the ledger for the complete picture.
/// </para>
/// </remarks>
public sealed class AzureDocumentIntelligenceAnalyzerSettings
{
    /// <summary>
    /// The Document Intelligence model to analyze with. <c>prebuilt-read</c> is the plain-text
    /// OCR model; the richer prebuilt models cost more per page.
    /// </summary>
    public string ModelId { get; set; } = "prebuilt-read";

    /// <summary>
    /// Price charged for one analyzed page, in whatever currency the rest of the ledger is
    /// denominated in. Multiplied by the analyzed page count to produce
    /// <c>CostEntry.Cost</c> — the ledger never prices anything itself.
    /// <para>
    /// <b>The default is indicative, not authoritative.</b> 0.0015 corresponds to the widely
    /// published pay-as-you-go rate for the Read model (USD 1.50 per 1,000 pages) at the time
    /// of writing. Azure pricing varies by tier, region, model and commitment, and changes
    /// without this library changing — set this from your own price sheet rather than
    /// trusting the default.
    /// </para>
    /// </summary>
    public decimal PricePerPage { get; set; } = 0.0015m;

    /// <summary>
    /// How long to wait between polls of the analyze long-running operation, when the service
    /// does not say otherwise. A <c>Retry-After</c> header on a poll response wins over this
    /// value, so raising it throttles polling while lowering it below what the service asks
    /// for does not speed anything up.
    /// <para>
    /// Exists because <c>DocumentIntelligenceClientOptions</c> cannot express a polling
    /// interval — its <c>Retry</c> settings govern transport retries, not long-running
    /// operation polling.
    /// </para>
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Optional BCP-47 locale hint (for example <c>en-US</c>) passed to the service. Leave
    /// <see langword="null"/> to let the service detect the language; a blank value is not sent.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Validates every configurable value and returns <paramref name="settings"/> unchanged.
    /// </summary>
    /// <param name="settings">The settings to check.</param>
    /// <param name="paramName">
    /// The name to blame in the thrown exception — the constructor parameter when a caller
    /// constructed the analyzer directly, <c>configure</c> when a builder extension did.
    /// </param>
    /// <returns><paramref name="settings"/>, so a caller can validate and assign in one step.</returns>
    /// <remarks>
    /// Public so builder extensions can run it at <i>registration</i> time. Leaving it to the
    /// constructor would defer every check into a DI factory the container does not invoke until
    /// first resolution, so a negative price would register silently and surface much later.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="ModelId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PricePerPage"/> or <see cref="PollingInterval"/> is negative.
    /// </exception>
    public static AzureDocumentIntelligenceAnalyzerSettings Validate(
        AzureDocumentIntelligenceAnalyzerSettings settings, string paramName)
    {
        ArgumentNullException.ThrowIfNull(settings, paramName);

        if (string.IsNullOrWhiteSpace(settings.ModelId))
        {
            throw new ArgumentException(
                $"{nameof(AzureDocumentIntelligenceAnalyzerSettings)}.{nameof(ModelId)} " +
                "must be a non-empty string.",
                paramName);
        }

        if (settings.PricePerPage < 0m)
        {
            throw new ArgumentOutOfRangeException(paramName, settings.PricePerPage,
                $"{nameof(AzureDocumentIntelligenceAnalyzerSettings)}.{nameof(PricePerPage)} " +
                "must not be negative.");
        }

        if (settings.PollingInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, settings.PollingInterval,
                $"{nameof(AzureDocumentIntelligenceAnalyzerSettings)}.{nameof(PollingInterval)} " +
                "must not be negative.");
        }

        return settings;
    }
}
