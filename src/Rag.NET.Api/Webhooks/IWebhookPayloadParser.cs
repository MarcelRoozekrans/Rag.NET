using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Rag.NET.Models;

namespace Rag.NET.Api.Webhooks;

/// <summary>
/// Converts a webhook JSON payload into ingestion jobs. Register a custom implementation
/// BEFORE calling <c>AddRagNetWebhooks</c> to replace the built-in
/// <see cref="GenericWebhookPayloadParser"/> (the default is registered with <c>TryAdd</c>,
/// so an earlier registration wins). Provider-specific parsers (GitHub, Notion, Slack)
/// are connector-package extension points.
/// </summary>
public interface IWebhookPayloadParser
{
    /// <summary>
    /// Attempts to convert <paramref name="payload"/> into one or more ingestion jobs.
    /// Returns <see langword="false"/> when the payload does not match the expected shape
    /// (the endpoint responds 400 Bad Request).
    /// </summary>
    bool TryParse(JsonElement payload, [NotNullWhen(true)] out IReadOnlyList<IngestionJob>? jobs);
}
