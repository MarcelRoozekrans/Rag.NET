using System.Globalization;
using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Wraps the most recent registration of a service in a decorator while preserving the
/// original implementation (instance, factory, or type) as the inner service. Shared by
/// the resilience <c>Use*</c> extensions so they can decorate whatever the user registered.
/// </summary>
/// <remarks>
/// The original registration's lifetime is preserved: a scoped service stays scoped after
/// decoration (one decorated instance per scope), a singleton stays a singleton. Keyed
/// registrations of the service type are ignored entirely — only the last non-keyed
/// registration is decorated. Ownership follows the container's own rules: inners the
/// container materialises (factory or type registrations) are re-registered internally so
/// the container still disposes them, while instance registrations remain externally
/// owned (the container never owned them) — decorators themselves are deliberately
/// non-owning either way.
/// </remarks>
internal static class ServiceDecorationHelper
{
    /// <summary>
    /// Replaces the last non-keyed <typeparamref name="TService"/> descriptor with a factory
    /// (same lifetime) that materialises the original registration and passes it through
    /// <paramref name="decorate"/>. Earlier registrations of the same service are untouched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No <typeparamref name="TService"/> registration exists yet: decorating extensions must
    /// be called after the underlying registration.
    /// </exception>
    internal static void Decorate<TService>(
        IServiceCollection services,
        Func<TService, IServiceProvider, TService> decorate)
        where TService : class
    {
        var original = FindLast<TService>(services) ?? throw MissingRegistration<TService>();
        services.Remove(original);

        if (original.ImplementationInstance is TService instance)
        {
            // The container never owned an instance registration; keep it externally owned.
            services.Add(ServiceDescriptor.Describe(
                typeof(TService), sp => decorate(instance, sp), original.Lifetime));
            return;
        }

        // Factory/type registrations produce container-owned inners: re-register the
        // materialised inner under a key — unique per call so stacked decorations cannot
        // collide or resolve recursively — so the container still tracks and disposes it.
        string innerKey = string.Create(CultureInfo.InvariantCulture,
            $"ragnet.decorated.inner.{FriendlyName(typeof(TService))}.{Guid.NewGuid():N}");
        services.Add(ServiceDescriptor.DescribeKeyed(
            typeof(TService), innerKey,
            (sp, _) => MaterialiseOriginal<TService>(sp, original), original.Lifetime));
        services.Add(ServiceDescriptor.Describe(
            typeof(TService),
            sp => decorate(sp.GetRequiredKeyedService<TService>(innerKey), sp),
            original.Lifetime));
    }

    /// <summary>
    /// Throws the same actionable error as <see cref="Decorate{TService}"/> when no non-keyed
    /// <typeparamref name="TService"/> registration exists, without mutating the collection.
    /// Lets callers pre-check every service they intend to decorate before decorating any,
    /// so a validation failure leaves no half-applied state.
    /// </summary>
    /// <exception cref="InvalidOperationException">No registration exists.</exception>
    internal static void EnsureRegistered<TService>(IServiceCollection services)
        where TService : class
    {
        if (FindLast<TService>(services) is null)
        {
            throw MissingRegistration<TService>();
        }
    }

    private static ServiceDescriptor? FindLast<TService>(IServiceCollection services)
    {
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var candidate = services[i];
            if (!candidate.IsKeyedService && candidate.ServiceType == typeof(TService))
            {
                return candidate;
            }
        }

        return null;
    }

    private static InvalidOperationException MissingRegistration<TService>()
    {
        var serviceName = FriendlyName(typeof(TService));
        return new InvalidOperationException(
            $"Cannot decorate {serviceName}: no prior registration was found. " +
            $"Register the underlying {serviceName} (e.g. your provider client) before " +
            "the decorating Use* extension — decoration wraps whatever is registered at that point.");
    }

    /// <summary>Type name without the generic-arity suffix (e.g. <c>IEmbeddingGenerator</c>, not <c>IEmbeddingGenerator`2</c>).</summary>
    private static string FriendlyName(Type type) =>
        type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;

    private static TService MaterialiseOriginal<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        where TService : class
    {
        if (descriptor.ImplementationFactory is { } factory)
        {
            return (TService)factory(serviceProvider);
        }

        return (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!);
    }
}
