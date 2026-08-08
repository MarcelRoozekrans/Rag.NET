using Microsoft.Extensions.Logging;

namespace Rag.NET.Hosting.Logging;

/// <summary>Source-generated log messages for <c>Rag.NET.Hosting</c>'s configuration wiring.</summary>
internal static partial class HostingLog
{
    /// <summary>
    /// Logged when <c>RagNet:VectorStore:Kind</c> resolves to <c>InMemory</c> — explicitly
    /// configured or left unset, both of which default to it. This is the phase's guard
    /// against repeating <c>UseCostBudgeting()</c>'s in-memory cost-ledger silence: an
    /// <c>InMemory</c> store looks like it works until the process restarts and every
    /// ingested document is gone, so the default must say so rather than being quietly
    /// convenient.
    /// </summary>
    [LoggerMessage(
        EventId = 384715926,
        EventName = "in_memory_vector_store_is_the_default",
        Level = LogLevel.Warning,
        Message = "RagNet:VectorStore:Kind is InMemory: every ingested document is lost when " +
            "the process exits. Set RagNet:VectorStore:Kind to Qdrant or PgVector for a vector " +
            "store that survives a restart.")]
    internal static partial void InMemoryVectorStoreIsTheDefault(ILogger logger);
}
