using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Abstractions;
using Rag.NET.Storage;

namespace Rag.NET.GraphRag;

/// <summary>Configures where the graph's own chunks are stored (#247).</summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="GraphStoreBuilder"/>, which configures where the graph's <i>structure</i>
/// lives. The two are separate because they hold different things: the graph store holds entities
/// and edges for traversal, this one holds their embeddings for search.
/// </para>
/// <para>
/// <b>The default is a separate in-memory store, and that is a decision with a cost.</b> It keeps
/// <c>UseGraphRag()</c> working with no extra configuration, and it means graph chunks are discarded
/// when the process exits while a configured graph store persists — so the two halves disagree after
/// a restart until the next ingest. <see cref="RagBuilderExtensions.UseGraphRag"/> warns when it
/// detects that mismatch rather than leaving it to be discovered from an empty global search.
/// </para>
/// </remarks>
/// <param name="services">The collection to register into.</param>
public sealed class GraphChunkStoreBuilder(IServiceCollection services)
{
    /// <summary>Stores the graph's chunks in the given store.</summary>
    /// <param name="store">A store of its own — not the one holding your documents.</param>
    /// <returns>This builder.</returns>
    public GraphChunkStoreBuilder Use(IVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        services.AddSingleton(new GraphChunkStore(store));
        return this;
    }

    /// <summary>Stores the graph's chunks in a store resolved from the container.</summary>
    /// <remarks>
    /// For a store that needs its own dependencies — a client, a collection name — registered
    /// separately and resolved here.
    /// </remarks>
    /// <param name="factory">Produces the store.</param>
    /// <returns>This builder.</returns>
    public GraphChunkStoreBuilder Use(Func<IServiceProvider, IVectorStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        services.AddSingleton(sp => new GraphChunkStore(factory(sp)));
        return this;
    }

    /// <summary>Falls back to a separate in-memory store when nothing else was configured.</summary>
    internal void UseInMemoryIfUnset() =>
        services.TryAddSingleton(_ => new GraphChunkStore(new InMemoryVectorStore()));
}
