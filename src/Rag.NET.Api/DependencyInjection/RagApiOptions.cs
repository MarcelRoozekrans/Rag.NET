namespace Rag.NET.Api.DependencyInjection;

public sealed class RagApiOptions
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>
    /// Explicit opt-out of API-key authentication, <see langword="false"/> by default.
    /// <c>AddRagNetApi</c> requires either a non-empty <see cref="ApiKeys"/> or this flag —
    /// never both, and never neither — so an unauthenticated API is always a decision visible
    /// in the registration code, not the silent result of forgetting to configure keys.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    public string RoutePrefix { get; set; } = "/rag";
}
