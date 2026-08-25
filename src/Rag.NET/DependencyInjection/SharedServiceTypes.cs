using System.Diagnostics;
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
    /// A non-singleton, non-generic, non-keyed descriptor is recorded (so <c>BuildFactory</c> can
    /// still skip it safely) and also traced as a warning here, at the call that declared it — this
    /// repository's usual eager-validation style. It deliberately does not <em>throw</em>: a
    /// composite registration helper like <c>AddHttpClient()</c> bundles a transient
    /// <c>HttpClient</c> alongside the singleton <c>IHttpClientFactory</c> that forwarding actually
    /// exists to share, and throwing here would make sharing that helper — the concrete scenario
    /// this type exists to support — impossible rather than merely partial.
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
            WarnIfUnshareable(serviceType, descriptor);
        }
    }

    /// <summary>Traces a warning for a descriptor <c>BuildFactory</c> will skip forwarding.</summary>
    /// <param name="serviceType">The declared service type.</param>
    /// <param name="descriptor">Its registration.</param>
    private static void WarnIfUnshareable(Type serviceType, ServiceDescriptor descriptor)
    {
        if (serviceType.IsGenericTypeDefinition || descriptor.IsKeyedService)
        {
            // Not diagnosed: an open generic or keyed registration is never what the caller meant
            // to share directly (see BuildFactory's remarks) — only its closed, resolved consumer
            // (e.g. IHttpClientFactory) is, and that is forwarded normally.
            return;
        }

        if (descriptor.Lifetime == ServiceLifetime.Singleton)
        {
            return;
        }

        Trace.TraceWarning(
            $"Rag.NET: AddRagNetShared declared '{serviceType}' shared, but it is registered with "
            + $"{descriptor.Lifetime} lifetime. Only Singleton services are forwarded to named "
            + "pipelines, so this type will not be reachable from any named pipeline's provider — "
            + "resolving it there fails with the usual \"no service registered\" error. If a related "
            + "singleton covers what you actually need (for example IHttpClientFactory alongside a "
            + "transient HttpClient), no action is required.");
    }
}
