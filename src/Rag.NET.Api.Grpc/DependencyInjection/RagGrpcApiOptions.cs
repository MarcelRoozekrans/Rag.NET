namespace Rag.NET.Api.Grpc.DependencyInjection;

public sealed class RagGrpcApiOptions
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>
    /// Explicit opt-out of API-key authentication, <see langword="false"/> by default.
    /// <c>AddRagNetGrpcApi</c> requires either a non-empty <see cref="ApiKeys"/> or this flag —
    /// never both, and never neither — so an unauthenticated service is always a decision
    /// visible in the registration code, not the silent result of forgetting to configure keys.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Lets clients set the fields that shape the prompt: <c>system_prompt</c>, <c>temperature</c>,
    /// <c>conversation_history</c>, and the map-reduce and refine prompt templates.
    /// <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// While it is off, an ask call that sets one of them fails with <c>InvalidArgument</c>, and the
    /// message names the field. Nothing is dropped silently. Retrieval fields are always accepted (#944).
    /// </remarks>
    public bool AllowClientPromptOverrides { get; set; }
}
