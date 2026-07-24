namespace Rag.NET.Api.Authentication;

public sealed class ApiKeyOptions
{
    public string[] ApiKeys { get; set; } = [];

    /// <summary>
    /// Request path prefixes exempt from API-key checks. Populated by
    /// <c>AddRagNetWebhooks</c> with the webhook route prefix: webhook requests are
    /// authenticated by their HMAC signature instead of the API key.
    /// </summary>
    public string[] ExemptPathPrefixes { get; set; } = [];
}
