using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// The service types <c>AddRagNetShared</c> declared, which every named pipeline forwards to the
/// root provider instead of registering for itself.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton on the root collection so that <c>AddRagNet(name, …)</c> can read it
/// regardless of call order — a named block may be declared before the shared block.
/// </para>
/// <para>
/// A set rather than a list, because declaring the same service type shared twice must forward once:
/// duplicate descriptors in a child collection make <c>IEnumerable&lt;T&gt;</c> resolution return it
/// twice, which is the kind of silent doubling that does not fail until something counts.
/// </para>
/// </remarks>
internal sealed class SharedServiceTypes
{
    private readonly List<SharedServiceEntry> _entries = [];
    private readonly HashSet<Type> _seen = [];

    /// <summary>The declared entries, in declaration order.</summary>
    public IReadOnlyList<SharedServiceEntry> Entries => _entries;

    /// <summary>Records <paramref name="descriptors"/>, ignoring any service type already recorded.</summary>
    /// <param name="descriptors">The descriptors <c>AddRagNetShared</c>'s callback registered.</param>
    /// <remarks>
    /// A non-singleton, non-generic, non-keyed descriptor is recorded too, so <c>BuildFactory</c>
    /// can still skip it safely rather than forward it. Nothing here diagnoses that case: a
    /// composite registration helper like <c>AddHttpClient()</c> bundles a transient
    /// <c>HttpClient</c> alongside the singleton <c>IHttpClientFactory</c> that forwarding actually
    /// exists to share, so most of what would be flagged is exactly that shape, authored by code
    /// the caller did not write. A named pipeline that actually needs a skipped type fails loudly
    /// on first resolution — the usual "no service registered" — which is where
    /// <see cref="ServiceCollectionExtensions.AddRagNetShared"/>'s remarks send the reader.
    /// </remarks>
    public void AddRange(IEnumerable<ServiceDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        foreach (var descriptor in descriptors)
        {
            var serviceType = descriptor.ServiceType;
            if (!_seen.Add(serviceType))
            {
                continue;
            }

            _entries.Add(new SharedServiceEntry(serviceType, descriptor.Lifetime, descriptor.IsKeyedService));
        }
    }
}
