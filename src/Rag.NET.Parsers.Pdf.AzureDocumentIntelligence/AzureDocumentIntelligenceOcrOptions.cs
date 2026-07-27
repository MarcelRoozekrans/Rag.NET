namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

/// <summary>
/// Tuning for <see cref="AzureDocumentIntelligenceOcrEngine"/>.
/// </summary>
/// <remarks>
/// <b>Telemetry asymmetry, stated deliberately.</b> OCR spend is written to
/// <c>ICostLedger</c>, so it counts toward the same spend window <c>UseCostBudgeting</c>
/// enforces for chat and embedding calls — enabling OCR can therefore trip *those* gates.
/// It does <b>not</b> emit the <c>ragnet.llm.cost</c> / <c>ragnet.llm.tokens</c> meters that
/// chat and embedding calls emit, because the type that publishes them (<c>CostAccounting</c>
/// in <c>Rag.NET</c>) is <see langword="internal"/> to that assembly and this package cannot
/// reach it. Dashboards built on those meters will under-report total spend by exactly the
/// OCR portion; query the ledger for the complete picture. Accepted for this phase.
/// </remarks>
public sealed class AzureDocumentIntelligenceOcrOptions
{
    /// <summary>
    /// The Document Intelligence model to analyze with. <c>prebuilt-read</c> is the plain-text
    /// OCR model and the right default here: the parser wants page text, and the richer
    /// prebuilt models cost more per page for structure this package deliberately discards.
    /// </summary>
    public string ModelId { get; set; } = "prebuilt-read";

    /// <summary>
    /// Price charged for one analyzed page, in whatever currency the rest of the ledger is
    /// denominated in. Multiplied by the billed page count to produce
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
    /// operation polling. Tests drive it to near-zero so a cassette run does not sit for tens
    /// of seconds.
    /// </para>
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Optional BCP-47 locale hint (for example <c>en-US</c>) passed to the service. Leave
    /// <see langword="null"/> to let the service detect the language.
    /// </summary>
    public string? Locale { get; set; }
}
