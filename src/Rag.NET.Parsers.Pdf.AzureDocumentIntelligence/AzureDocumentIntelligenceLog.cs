using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

internal static partial class AzureDocumentIntelligenceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Recording {Pages} OCR page(s) to the cost ledger failed; the OCR result is unaffected but this spend is missing from the budget window")]
    internal static partial void CostLedgerWriteFailed(ILogger logger, int pages, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Azure Document Intelligence analyzed {BilledPages} page(s) with model {ModelId}; {RecognizedPages} page(s) returned text")]
    internal static partial void Analyzed(ILogger logger, int billedPages, string modelId, int recognizedPages);
}
