using System.Diagnostics;
using System.Reflection;

namespace Rag.NET.Telemetry;

internal static class RagActivitySource
{
    internal static readonly ActivitySource Source = new(
        "Rag.NET",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    internal const string IngestActivity = "ingest";
    internal const string RetrieveActivity = "retrieve";
    internal const string AskActivity = "ask";
}
