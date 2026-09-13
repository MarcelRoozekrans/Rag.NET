using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>Chooses where the entity graph lives.</summary>
public sealed partial class GraphStoreBuilder(IServiceCollection services)
{
    /// <summary>Stores the graph in a SQLite database at <paramref name="dbPath"/>.</summary>
    /// <param name="dbPath">A file path, or <c>:memory:</c> for an ephemeral graph.</param>
    /// <returns>This builder.</returns>
    public GraphStoreBuilder UseSqlite(string dbPath)
    {
        services.AddSingleton<IGraphStore>(_ => new SqliteGraphStore(dbPath));
        return this;
    }

    /// <summary>
    /// Stores the graph in memory, warning that it will not survive the process.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// <para>
    /// <b>What an unconfigured caller gets, and why it warns.</b> <c>UseGraphRag()</c> without a
    /// graph store lands here, and an in-memory graph is discarded at process exit and rebuilt on
    /// the next ingest. That rebuild is not free: <b>22 m 18 s of graph construction</b> on the
    /// MultiHop-RAG corpus, measured. Nothing said so before —
    /// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/298">#298</see> raised it, and
    /// the default was silent in code and absent from every published page.
    /// </para>
    /// <para>
    /// <b>Why a warning rather than a refusal.</b> Refusing to start without an explicit store
    /// would tax every quick experiment to fix a production concern. The default stays convenient;
    /// what changes is that it stops being invisible. This is the same posture the security pages
    /// take toward defaults that fail open: acceptable when visible, not when silent.
    /// </para>
    /// <para>
    /// <b>Logged at resolution, not registration.</b> The registration extension has no
    /// <see cref="ILogger"/> to write to. The factory below runs once per container because the
    /// registration is a singleton, so the warning appears once when the graph is first needed
    /// rather than once per ingest.
    /// </para>
    /// </remarks>
    public GraphStoreBuilder UseEphemeralInMemory()
    {
        services.AddSingleton<IGraphStore>(sp =>
        {
            LogEphemeralGraphStore(
                sp.GetService<ILogger<GraphStoreBuilder>>()
                ?? NullLogger<GraphStoreBuilder>.Instance);

            return new SqliteGraphStore(":memory:");
        });

        return this;
    }

    [LoggerMessage(
        EventId = 1502773641,
        EventName = "log_ephemeral_graph_store",
        Level = LogLevel.Warning,
        Message = "GraphRAG has no graph store configured, so the entity graph is being kept in " +
                  "memory and will be discarded when this process exits. The next ingest rebuilds " +
                  "it from scratch, which took 22 minutes on a 609-document corpus. Call " +
                  "UseGraphRag(graph: g => g.UseSqlite(\"graph.db\")) to keep it.")]
    private static partial void LogEphemeralGraphStore(ILogger logger);
}
