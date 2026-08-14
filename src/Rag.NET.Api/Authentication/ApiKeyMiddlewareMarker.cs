namespace Rag.NET.Api.Authentication;

/// <summary>
/// Records that <c>UseRagNetApiAuthentication</c> put <see cref="ApiKeyMiddleware"/> into the
/// request pipeline, so <c>MapRagNetApi</c> can refuse to map endpoints that nothing would
/// authenticate.
/// </summary>
/// <remarks>
/// The two ends of that contract cannot see each other directly: the middleware is added to an
/// <c>IApplicationBuilder</c> and the endpoints to an
/// <see cref="Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"/>, and neither exposes the
/// other's pipeline. The container is the one thing both hold, so the fact travels through this
/// singleton — registered by <c>AddRagNetApi</c>, set by the <c>Use</c> call, read at mapping
/// time. Both writes and the read happen on the startup thread while the pipeline is being
/// assembled, before any request is served, so the unsynchronised flag needs no locking.
/// </remarks>
internal sealed class ApiKeyMiddlewareMarker
{
    /// <summary>
    /// <see langword="true"/> once <c>UseRagNetApiAuthentication</c> has run. Absence of the
    /// marker service itself means <c>AddRagNetApi</c> was never called, which is a separate
    /// failure with its own message.
    /// </summary>
    public bool IsRegistered { get; private set; }

    public void MarkRegistered() => IsRegistered = true;
}
