using Microsoft.Extensions.Logging;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

internal static partial class AzureDocumentIntelligenceLog
{
    [LoggerMessage(
        EventId = 481128757, EventName = "analyzed",
        Level = LogLevel.Debug,
        Message = "Azure Document Intelligence analyzed {BilledPages} page(s) with model {ModelId}; {RecognizedPages} page(s) returned text")]
    internal static partial void Analyzed(ILogger logger, int billedPages, string modelId, int recognizedPages);
}
