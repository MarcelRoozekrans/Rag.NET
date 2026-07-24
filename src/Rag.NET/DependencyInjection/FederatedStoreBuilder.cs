using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;
using Rag.NET.Storage;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Collects the stores that make up a <see cref="FederatedVectorStore"/>.
/// Used by <c>UseFederatedSearch</c>; the configuration is validated when the
/// extension returns, and the stores themselves are created lazily when the
/// federated store is first resolved.
/// </summary>
public sealed class FederatedStoreBuilder
{
    private readonly List<Func<IServiceProvider, IVectorStore>> _factories = [];
    private readonly List<string?> _names = [];
    private int _primaryIndex;
    private int _rrfK = 60;
    private bool _anyNamed;

    /// <summary>
    /// Adds a store to the federation. The factory runs once, when the federated store
    /// is first resolved — resolve shared services from the provided
    /// <see cref="IServiceProvider"/>, or capture a pre-built store instance.
    /// </summary>
    /// <param name="factory">Factory producing the store to federate.</param>
    /// <param name="name">
    /// Optional label used for the <c>source.store</c> metadata tag and error messages.
    /// Unnamed stores are labelled by their zero-based index.
    /// </param>
    public FederatedStoreBuilder AddStore(Func<IServiceProvider, IVectorStore> factory, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory);
        _names.Add(name);
        _anyNamed |= name is not null;
        return this;
    }

    /// <summary>
    /// Selects which store (zero-based index, in <see cref="AddStore"/> order) receives
    /// writes and deletes. Default: the first store.
    /// </summary>
    public FederatedStoreBuilder WithPrimary(int index)
    {
        _primaryIndex = index;
        return this;
    }

    /// <summary>
    /// Sets the Reciprocal Rank Fusion constant <c>k</c> used when merging per-store
    /// rankings. Default 60 (see <see cref="FederatedStoreOptions.RrfK"/>).
    /// </summary>
    public FederatedStoreBuilder WithRrfK(int rrfK)
    {
        _rrfK = rrfK;
        return this;
    }

    internal void Validate()
    {
        if (_factories.Count < 2)
        {
            throw new InvalidOperationException(
                $"UseFederatedSearch requires at least 2 stores, got {_factories.Count}. Add stores via {nameof(AddStore)}, or register the single store directly.");
        }
        if (_primaryIndex < 0 || _primaryIndex >= _factories.Count)
        {
            throw new InvalidOperationException(
                $"UseFederatedSearch primary index {_primaryIndex} is out of bounds for {_factories.Count} store(s).");
        }
        if (_rrfK < 1)
        {
            throw new InvalidOperationException(
                $"UseFederatedSearch RrfK ({_rrfK}) must be at least 1.");
        }
    }

    internal FederatedVectorStore Build(IServiceProvider serviceProvider)
    {
        var stores = new IVectorStore[_factories.Count];
        for (int i = 0; i < stores.Length; i++)
            stores[i] = _factories[i](serviceProvider);

        // Unnamed stores fall back to their zero-based index as the label; when no
        // store is named at all, StoreNames stays null (all-index labels).
        IReadOnlyList<string>? storeNames = null;
        if (_anyNamed)
        {
            var names = new string[_names.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = _names[i] ?? i.ToString(CultureInfo.InvariantCulture);
            storeNames = names;
        }

        var options = new FederatedStoreOptions
        {
            PrimaryIndex = _primaryIndex,
            RrfK = _rrfK,
            StoreNames = storeNames,
        };

        return new FederatedVectorStore(stores, options, serviceProvider.GetService<ILogger<FederatedVectorStore>>());
    }
}
