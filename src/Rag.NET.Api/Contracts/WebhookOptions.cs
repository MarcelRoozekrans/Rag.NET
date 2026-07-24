namespace Rag.NET.Api.Contracts;

/// <summary>Options for the HMAC-verified webhook ingestion endpoint.</summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Shared secret for HMAC-SHA256 signature verification over the raw request body.
    /// Required and non-empty — validated by <c>AddRagNetWebhooks</c>.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Request header carrying the hex-encoded HMAC-SHA256 signature.
    /// A GitHub-style <c>sha256=</c> prefix in the header value is tolerated.
    /// Default <c>X-Signature-256</c>.
    /// </summary>
    public string SignatureHeader { get; set; } = "X-Signature-256";

    /// <summary>
    /// Route prefix for webhook endpoints (POST <c>{RoutePrefix}/ingest</c>).
    /// This prefix is exempted from API-key authentication — the HMAC signature
    /// replaces the key for webhook callers. Default <c>/rag/webhooks</c>.
    /// </summary>
    public string RoutePrefix { get; set; } = "/rag/webhooks";
}
