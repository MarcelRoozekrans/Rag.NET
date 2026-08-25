using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.DependencyInjection;

/// <summary>Builds and owns one child <see cref="IServiceProvider"/> per named pipeline.</summary>
/// <remarks>
/// <para>
/// <b>Children are built lazily, on first <see cref="Get"/>.</b> At registration time the root
/// provider does not exist, so there is nothing for a child's forwarded services to resolve from.
/// The cost is that a misconfigured named pipeline surfaces on first use rather than at startup.
/// </para>
/// <para>
/// <b>Ownership runs one way.</b> Shared services live in the root and are disposed by it, never by
/// a child — so tearing down one pipeline cannot pull the embedding model out from under another.
/// Both <see cref="Dispose"/> and <see cref="DisposeAsync"/> are supported because this factory is
/// itself a root-container singleton: a host that disposes its root provider synchronously must be
/// able to tear this down synchronously too. Each child is a concrete <c>ServiceProvider</c>, which
/// supports both; a child whose own registrations include a service that implements only
/// <see cref="IAsyncDisposable"/> will still make synchronous disposal throw for that child, exactly
/// as a plain <c>ServiceProvider</c> would.
/// </para>
/// </remarks>
/// <param name="collections">Each name's composed service collection.</param>
/// <param name="rootProvider">The root provider, which forwarded services resolve from.</param>
internal sealed class RagPipelineFactory(
    IReadOnlyDictionary<string, IServiceCollection> collections,
    IServiceProvider rootProvider) : IRagPipelineFactory, IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ServiceProvider> _providers = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>
    /// The root provider forwarded services resolve from. Exposed only as a test seam; forwarding
    /// itself is wired into each child's descriptors before construction, not read from here.
    /// </summary>
    internal IServiceProvider RootProvider => rootProvider;

    /// <inheritdoc />
    public bool Contains(string name) => collections.ContainsKey(name);

    /// <inheritdoc />
    public IRagPipeline Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ProviderFor(name).GetRequiredService<IRagPipeline>();
    }

    /// <summary>The child provider for <paramref name="name"/>, building it on first use.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <returns>That name's provider.</returns>
    /// <exception cref="ArgumentException">No pipeline was registered under that name.</exception>
    internal ServiceProvider ProviderFor(string name)
    {
        lock (_lock)
        {
            if (_providers.TryGetValue(name, out var existing))
            {
                return existing;
            }

            if (!collections.TryGetValue(name, out var collection))
            {
                throw new ArgumentException(
                    $"No RAG pipeline is registered under the name '{name}'. "
                    + "Register one with services.AddRagNet(\"" + name + "\", rag => …).",
                    nameof(name));
            }

            var built = collection.BuildServiceProvider();
            _providers[name] = built;
            return built;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var provider in _providers.Values)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        _providers.Clear();
    }
}
