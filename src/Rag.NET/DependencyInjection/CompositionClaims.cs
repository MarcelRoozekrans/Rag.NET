using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.DependencyInjection;

/// <summary>
/// Records that a <c>Use*</c>/<c>Configure*</c> extension intends to decorate a service, and turns
/// "it decorated nothing" into an exception when the pipeline is resolved.
/// </summary>
/// <remarks>
/// <para>
/// The extensions that wrap <c>IChatClient</c>, <c>IEmbeddingGenerator</c> and <c>IVectorStore</c>
/// rewrite descriptors, so they can only wrap what is registered when they run. A surface registered
/// <i>afterwards</i> — or a later registration that replaces the decorated one — leaves the feature
/// silently absent: no retries, no budget, no failover (issue #195). Those types are resolved
/// directly all over the pipeline, so the decoration cannot be deferred the way an answer-engine
/// decoration can (see <see cref="AnswerEngineDecorationBuilder"/>); the honest remedy is to fail
/// loudly instead.
/// </para>
/// <para>
/// <b>Why a runtime probe rather than descriptor bookkeeping.</b> A later <i>decoration</i> of the
/// same surface (<c>ConfigureResilience</c> after <c>UseRateLimiting</c>, say) also replaces the
/// last descriptor while leaving the earlier decorator perfectly reachable underneath it. Nothing in
/// the collection tells that apart from a replacement. Resolving the surface and asking the
/// decorator whether it was actually constructed does, and cannot report a working composition as
/// broken.
/// </para>
/// </remarks>
internal static class CompositionClaims
{
    /// <summary>
    /// Claims <typeparamref name="TService"/> for <paramref name="calledBy"/> and returns the probe
    /// its decorator factory must mark.
    /// </summary>
    /// <typeparam name="TService">The surface being decorated.</typeparam>
    /// <param name="services">The collection the extension is registering into.</param>
    /// <param name="calledBy">The extension method making the claim, named in the failure message.</param>
    /// <returns>
    /// The probe to set from inside the decorator's factory. A claim whose surface is not registered
    /// yet takes a probe nobody marks — which is exactly how "registered afterwards" is detected.
    /// </returns>
    internal static DecorationProbe Claim<TService>(IServiceCollection services, string calledBy)
        where TService : class =>
        Registry(services).Claim(typeof(TService), calledBy);

    /// <summary>Finds the registry, or creates and registers it on first use.</summary>
    /// <param name="services">The collection to search.</param>
    /// <returns>The registry every claim on this collection is recorded in.</returns>
    private static CompositionClaimRegistry Registry(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ImplementationInstance is CompositionClaimRegistry existing)
                return existing;
        }

        var registry = new CompositionClaimRegistry(services);
        services.AddSingleton(registry);

        return registry;
    }
}
