using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>Every claim made against one service collection, and the check they add up to.</summary>
/// <param name="services">
/// The collection itself, read again at validation time: that is the only way to see registrations
/// made after the claiming extension ran.
/// </param>
internal sealed class CompositionClaimRegistry(IServiceCollection services)
{
    private readonly List<SurfaceClaim> _claims = [];

    /// <summary>Records a claim, or returns the probe of an identical one already recorded.</summary>
    /// <param name="surface">The service type being decorated.</param>
    /// <param name="calledBy">The extension method claiming it.</param>
    /// <returns>The probe belonging to this (surface, caller) pair.</returns>
    /// <remarks>
    /// Idempotent per pair, so an extension called twice — legal for every idempotent <c>Use*</c> —
    /// does not leave a first, unmarkable probe behind and report its own repeat call as a defect.
    /// </remarks>
    internal DecorationProbe Claim(Type surface, string calledBy)
    {
        for (var i = 0; i < _claims.Count; i++)
        {
            if (_claims[i].Surface == surface && string.Equals(_claims[i].CalledBy, calledBy, StringComparison.Ordinal))
                return _claims[i].Probe;
        }

        var claim = new SurfaceClaim(surface, calledBy, new DecorationProbe());
        _claims.Add(claim);

        return claim.Probe;
    }

    /// <summary>Throws when a claimed surface resolves to something the claimant never wrapped.</summary>
    /// <param name="serviceProvider">The provider the pipeline is being built from.</param>
    /// <exception cref="InvalidOperationException">A decoration applies to nothing.</exception>
    internal void Validate(IServiceProvider serviceProvider)
    {
        for (var i = 0; i < _claims.Count; i++)
        {
            var claim = _claims[i];

            // Resolving is what runs the decorator factory, so the probe is only meaningful after.
            if (claim.Probe.Applied || !IsResolvableFromTheRoot(claim.Surface))
                continue;

            if (serviceProvider.GetService(claim.Surface) is null || claim.Probe.Applied)
                continue;

            throw new InvalidOperationException(Describe(claim));
        }
    }

    /// <summary>
    /// Whether the surface can be resolved from the root scope, which is where the pipeline
    /// singleton is built.
    /// </summary>
    /// <param name="surface">The service type to check.</param>
    /// <returns>
    /// <see langword="false"/> when any registration of it is scoped or transient — resolving a
    /// scoped service from the root throws, and a transient one would be constructed for nothing.
    /// Such a container is not checked at all rather than checked wrongly.
    /// </returns>
    private bool IsResolvableFromTheRoot(Type surface)
    {
        var found = false;
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.IsKeyedService || descriptor.ServiceType != surface)
                continue;

            if (descriptor.Lifetime != ServiceLifetime.Singleton)
                return false;

            found = true;
        }

        return found;
    }

    /// <summary>Names the call that did nothing, the surface it missed, and the order that fixes it.</summary>
    /// <param name="claim">The violated claim.</param>
    /// <returns>The message.</returns>
    private static string Describe(SurfaceClaim claim)
    {
        var surface = ServiceDecorationHelper.FriendlyName(claim.Surface);

        return $"{claim.CalledBy} is not applied to the {surface} this container resolves. It " +
            $"decorates whatever is registered at the moment it runs, and this {surface} was " +
            $"registered (or replaced) afterwards, so the feature {claim.CalledBy} configures is " +
            $"silently absent. Move the {claim.CalledBy} call after the {surface} registration. " +
            "This is checked when the RAG pipeline is resolved because a registration made later " +
            "cannot be seen at registration time.";
    }

    private sealed record SurfaceClaim(Type Surface, string CalledBy, DecorationProbe Probe);
}
