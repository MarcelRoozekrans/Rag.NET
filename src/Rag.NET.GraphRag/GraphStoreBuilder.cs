using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Graph;

namespace Rag.NET.GraphRag;

/// <summary>Builder for configuring the graph store backend.</summary>
public sealed class GraphStoreBuilder(IServiceCollection services)
{
    /// <summary>Use SQLite-backed graph store.</summary>
    public GraphStoreBuilder UseSqlite(string dbPath)
    {
        services.AddSingleton<IGraphStore>(_ => new SqliteGraphStore(dbPath));
        return this;
    }
}
