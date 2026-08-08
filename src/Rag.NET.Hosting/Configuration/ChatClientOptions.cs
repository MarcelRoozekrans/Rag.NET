namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Endpoint, key, and model for the OpenAI-compatible chat client — one shape covers OpenAI,
/// Azure OpenAI, OpenRouter, Ollama, and LM Studio, since all of them speak the same wire API.
/// </summary>
public sealed class ChatClientOptions
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://openrouter.ai/api/v1</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The API key. A provider that does not require one — a local Ollama or LM Studio instance
    /// — still needs a non-empty credential value; leaving this unset supplies a placeholder
    /// rather than failing before the setting even gets a chance to matter.
    /// <c>AddRagNetPipelineFromConfiguration</c> only allows this to be unset when
    /// <see cref="Endpoint"/> is a loopback address (<c>localhost</c>, <c>127.0.0.1</c>,
    /// <c>[::1]</c>); anything else requires an explicit key, so a forgotten credential is a
    /// startup error rather than a 401 from the provider.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model id to request, e.g. <c>gpt-4o-mini</c> or <c>llama3.2</c>.</summary>
    public string Model { get; set; } = string.Empty;
}
