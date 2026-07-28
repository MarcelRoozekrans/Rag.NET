using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Wraps <b>every</b> registration of a service in a decorator, preserving lifetime and order.
/// </summary>
/// <remarks>
/// <para>
/// <c>Rag.NET</c>'s own <c>ServiceDecorationHelper</c> — the idiom <c>ConfigureResilience</c> uses —
/// decorates the <i>last</i> registration, which is right for a service resolved as a single instance
/// like <c>IVectorStore</c>. It is wrong for all three surfaces this package decorates.
/// <c>RetrievalGuardBehavior.Guards</c>, <c>ChunkSanitiserBehavior.Sanitisers</c> and
/// <c>QuerySanitiserPipelineDecorator</c>'s constructor each take an <c>IEnumerable&lt;T&gt;</c>, so a
/// configuration with a regex guard, a trust-level guard and an RBAC guard has three registrations and
/// the container hands all three to the pipeline. Decorating only the last would have traced one guard
/// out of three and silently reported the other two as never having run — which is worse than tracing
/// none, because the trace would look complete.
/// </para>
/// <para>
/// Order is preserved. Every descriptor for the service is removed and re-added in the order it was
/// found, so all of them move to the end of the collection together and their positions relative to
/// each other — which is the order <c>GetServices</c> returns and the order a sanitiser chain applies
/// in — are unchanged.
/// </para>
/// <para>
/// Ownership follows <c>ServiceDecorationHelper</c>'s rules, including the one it had to learn the hard
/// way: an inner materialised from a factory or a type is re-registered under a <b>unique</b> key so
/// the container still disposes it, and the key must be unique per call or a stacked decoration
/// deadlocks inside the container's singleton lock. Instance registrations stay externally owned.
/// </para>
/// </remarks>
internal static class ServiceDecoration
{
    /// <summary>Decorates every non-keyed registration of <typeparamref name="TService"/>.</summary>
    /// <typeparam name="TService">The service being decorated.</typeparam>
    /// <param name="services">The collection to rewrite.</param>
    /// <param name="decorate">Builds the decorator around one materialised inner.</param>
    /// <returns>
    /// How many registrations were decorated. Zero is a legitimate outcome and the caller's cue that
    /// there was nothing to trace — registering diagnostics with no guards present is a no-op, not a
    /// misconfiguration.
    /// </returns>
    public static int DecorateAll<TService>(
        IServiceCollection services,
        Func<TService, IServiceProvider, TService> decorate)
        where TService : class
    {
        var originals = FindAll<TService>(services);

        for (var i = 0; i < originals.Count; i++)
            Replace(services, originals[i], decorate);

        return originals.Count;
    }

    /// <summary>Every non-keyed descriptor for a service, in registration order.</summary>
    /// <typeparam name="TService">The service to look for.</typeparam>
    /// <param name="services">The collection to scan.</param>
    /// <returns>The descriptors, copied out before anything is mutated.</returns>
    /// <remarks>
    /// Collected first rather than rewritten in place: <see cref="Replace"/> both removes and adds, so
    /// scanning and mutating at once would walk over descriptors this method had just written.
    /// </remarks>
    private static List<ServiceDescriptor> FindAll<TService>(IServiceCollection services)
    {
        var found = new List<ServiceDescriptor>();

        for (var i = 0; i < services.Count; i++)
        {
            var candidate = services[i];

            if (!candidate.IsKeyedService && candidate.ServiceType == typeof(TService))
                found.Add(candidate);
        }

        return found;
    }

    /// <summary>Swaps one descriptor for a factory that decorates what it used to produce.</summary>
    /// <typeparam name="TService">The service being decorated.</typeparam>
    /// <param name="services">The collection to rewrite.</param>
    /// <param name="original">The descriptor being replaced.</param>
    /// <param name="decorate">Builds the decorator around the materialised inner.</param>
    private static void Replace<TService>(
        IServiceCollection services,
        ServiceDescriptor original,
        Func<TService, IServiceProvider, TService> decorate)
        where TService : class
    {
        services.Remove(original);

        if (original.ImplementationInstance is TService instance)
        {
            // The container never owned an instance registration; keep it externally owned.
            services.Add(ServiceDescriptor.Describe(
                typeof(TService), sp => decorate(instance, sp), original.Lifetime));
            return;
        }

        var innerKey = string.Create(
            CultureInfo.InvariantCulture,
            $"ragnet.diagnostics.inner.{typeof(TService).Name}.{Guid.NewGuid():N}");

        services.Add(ServiceDescriptor.DescribeKeyed(
            typeof(TService), innerKey, (sp, _) => Materialise<TService>(sp, original), original.Lifetime));
        services.Add(ServiceDescriptor.Describe(
            typeof(TService), sp => decorate(sp.GetRequiredKeyedService<TService>(innerKey), sp), original.Lifetime));
    }

    /// <summary>Builds what the original descriptor would have built.</summary>
    /// <typeparam name="TService">The service being materialised.</typeparam>
    /// <param name="serviceProvider">The provider to resolve dependencies from.</param>
    /// <param name="descriptor">The original descriptor.</param>
    /// <returns>The undecorated implementation.</returns>
    private static TService Materialise<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        where TService : class =>
        descriptor.ImplementationFactory is { } factory
            ? (TService)factory(serviceProvider)
            : (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!);
}
