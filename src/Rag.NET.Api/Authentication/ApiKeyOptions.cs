namespace Rag.NET.Api.Authentication;

public sealed class ApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>
    /// Explicit opt-out of API-key authentication. <see langword="false"/> by default: with no
    /// keys configured and no opt-out, registration throws and the middleware fails closed
    /// (401) rather than silently serving every endpoint unauthenticated. Setting this together
    /// with a non-empty <see cref="ApiKeys"/> is rejected at registration; if the mutable
    /// options instance is later pushed into that state anyway, the configured keys win and
    /// requests without a valid key are still refused.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Request path prefixes exempt from API-key checks. Populated by
    /// <c>AddRagNetWebhooks</c> with the webhook route prefix: webhook requests are
    /// authenticated by their HMAC signature instead of the API key.
    /// </summary>
    public string[] ExemptPathPrefixes { get; set; } = [];
}
