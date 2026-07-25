using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Wraps the most recent registration of a service in a decorator while preserving the
/// original implementation (instance, factory, or type) as the inner service. Shared by
/// the resilience <c>Use*</c> extensions so they can decorate whatever the user registered.
/// </summary>
internal static class ServiceDecorationHelper
{
    /// <summary>
    /// Replaces the last <typeparamref name="TService"/> descriptor with a singleton factory
    /// that materialises the original registration and passes it through
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
        ServiceDescriptor? original = null;
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var candidate = services[i];
            if (!candidate.IsKeyedService && candidate.ServiceType == typeof(TService))
            {
                original = candidate;
                break;
            }
        }

        if (original is null)
        {
            var serviceName = FriendlyName(typeof(TService));
            throw new InvalidOperationException(
                $"Cannot decorate {serviceName}: no prior registration was found. " +
                $"Register the underlying {serviceName} (e.g. your provider client) before " +
                "the decorating Use* extension — decoration wraps whatever is registered at that point.");
        }

        services.Remove(original);
        services.AddSingleton(sp => decorate(MaterialiseOriginal<TService>(sp, original), sp));
    }

    /// <summary>Type name without the generic-arity suffix (e.g. <c>IEmbeddingGenerator</c>, not <c>IEmbeddingGenerator`2</c>).</summary>
    private static string FriendlyName(Type type) =>
        type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;

    private static TService MaterialiseOriginal<TService>(
        IServiceProvider serviceProvider,
        ServiceDescriptor descriptor)
        where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return (TService)factory(serviceProvider);
        }

        return (TService)ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!);
    }
}
