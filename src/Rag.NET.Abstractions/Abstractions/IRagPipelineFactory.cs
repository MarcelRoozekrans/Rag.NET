namespace Rag.NET.Abstractions;

/// <summary>Resolves the named <see cref="IRagPipeline"/>s registered by <c>AddRagNet(name, …)</c>.</summary>
/// <remarks>
/// <para>
/// Each named pipeline has its own service provider, so its vector store, BM25 index, caches and
/// behaviours are separate from every other name's. Services declared through
/// <c>AddRagNetShared</c> — an embedding model, an <c>IChatClient</c> — stay singular across all of
/// them.
/// </para>
/// <para>
/// The unnamed <c>AddRagNet</c> is unaffected: it registers into the root container and its pipeline
/// is still resolved with <c>GetRequiredService&lt;IRagPipeline&gt;()</c>. Named pipelines are
/// additive (#342).
/// </para>
/// </remarks>
public interface IRagPipelineFactory
{
    /// <summary>Gets the pipeline registered under <paramref name="name"/>.</summary>
    /// <param name="name">The name passed to <c>AddRagNet(name, …)</c>.</param>
    /// <returns>That name's pipeline. The same instance on every call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No pipeline was registered under that name.</exception>
    /// <exception cref="ObjectDisposedException">This factory has already been disposed.</exception>
    IRagPipeline Get(string name);

    /// <summary>Whether a pipeline was registered under <paramref name="name"/>.</summary>
    /// <param name="name">The name to look for.</param>
    /// <returns>
    /// <see langword="true"/> if a pipeline was registered under <paramref name="name"/>; otherwise
    /// <see langword="false"/>. Registration is all this checks: unlike <see cref="Get"/>, it does
    /// not throw once this factory has been disposed, and keeps returning <see langword="true"/> for
    /// a name that was registered even though <see cref="Get"/> would now throw
    /// <see cref="ObjectDisposedException"/>.
    /// </returns>
    bool Contains(string name);
}
