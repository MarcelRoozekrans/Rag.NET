using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rag.NET.Telemetry;

internal static class RagTelemetry
{
    internal const string SourceName = "Rag.NET";

    internal static readonly ActivitySource ActivitySource = new(SourceName, "1.0.0");
    internal static readonly Meter Meter = new(SourceName, "1.0.0");

    // Histograms
    internal static readonly Histogram<double> IngestDuration =
        Meter.CreateHistogram<double>("ragnet.ingest.duration", "ms", "Total ingestion time per document");
    internal static readonly Histogram<double> EmbedDuration =
        Meter.CreateHistogram<double>("ragnet.embed.duration", "ms", "Embedding generation time per batch");
    internal static readonly Histogram<double> RetrieveDuration =
        Meter.CreateHistogram<double>("ragnet.retrieve.duration", "ms", "End-to-end retrieval time per query");
    internal static readonly Histogram<double> AskDuration =
        Meter.CreateHistogram<double>("ragnet.ask.duration", "ms", "Answer generation time per query");
    // Tagged surface=chat|embedding and outcome=granted|rejected|cancelled|faulted
    // ("rejected" only for the two deliberate rejections — queue overflow and
    // over-capacity permits; unexpected failures record "faulted"). Recorded for
    // every acquire outcome — it measures time held at the limiter; rejection DETAILS remain
    // observable through the caller's exception handling, per the conservative-instrument
    // convention below.
    internal static readonly Histogram<double> RateLimitWaitDuration =
        Meter.CreateHistogram<double>("ragnet.ratelimit.wait.duration", "ms", "Time spent waiting for a rate-limit permit");

    // Counters
    internal static readonly Counter<long> ChunksStored =
        Meter.CreateCounter<long>("ragnet.chunks.stored", "chunks", "Total chunks written to the vector store");
    internal static readonly Counter<long> ChunksRetrieved =
        Meter.CreateCounter<long>("ragnet.chunks.retrieved", "chunks", "Total chunks returned by retrieval");
    internal static readonly Counter<long> IngestErrors =
        Meter.CreateCounter<long>("ragnet.ingest.errors", "errors", "Total ingestion failures");
    internal static readonly Counter<long> RetrieveErrors =
        Meter.CreateCounter<long>("ragnet.retrieve.errors", "errors", "Total retrieval failures");
    // ragnet.ask.errors is intentionally omitted: answer-generation failures are observable through
    // the ragnet.ask span status (ActivityStatusCode.Error) and the caller's exception handling.
    // An error counter would require exposing a public metric surface for a single call site.

    // Tagged direction=in|out and surface=chat|embedding (the same surface tag name as
    // ragnet.ratelimit.wait.duration). Counts are provider-reported when the response carries
    // full usage, tiktoken cl100k estimates otherwise — the same numbers the cost ledger
    // records. Per-call estimation DETAILS (which source was used) are deliberately not a
    // metric, per the conservative-instrument convention above.
    internal static readonly Counter<long> LlmTokens =
        Meter.CreateCounter<long>("ragnet.llm.tokens", "tokens", "LLM tokens consumed by chat and embedding calls");
    // Tagged surface=chat|embedding. Unit "usd" is nominal: values are in whatever currency the
    // user-supplied CostBudgetOptions prices are quoted in.
    internal static readonly Counter<double> LlmCost =
        Meter.CreateCounter<double>("ragnet.llm.cost", "usd", "LLM spend computed from configured prices");
}
