using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.AzureDocumentIntelligence;

internal static partial class AzureDocumentIntelligenceLog
{
    // The same event id, name, level and text as the PDF OCR engine's definition. The engine
    // delegates to the analyzer and passes its own logger, so a user filtering or alerting on
    // this event sees no change when the logic moved here.
    [LoggerMessage(
        EventId = 25359835, EventName = "cost_ledger_write_failed",
        Level = LogLevel.Warning,
        Message = "Recording {Pages} OCR page(s) to the cost ledger failed; the OCR result is unaffected but this spend is missing from the budget window")]
    internal static partial void CostLedgerWriteFailed(ILogger logger, int pages, Exception exception);
}
