using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace Rag.NET.Testing;

/// <summary>
/// Returns an IChatClient backed by OpenRouter when OPENROUTER_API_KEY is set,
/// or the provided Ollama fixture client as fallback.
/// </summary>
public static class TestChatClientFactory
{
    private static readonly string? ApiKey =
        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

    // OpenRouter delists models, and a delisted default fails the whole suite with an opaque
    // HTTP 404 "No endpoints found for <model>" rather than anything resembling a test failure.
    // The previous default, nvidia/llama-3.1-nemotron-70b-instruct, had left the catalogue
    // entirely and went unnoticed because nothing had exercised this path since. Prefer a
    // first-party open-weights model with many providers behind it, and re-check this against
    // https://openrouter.ai/api/v1/models when it starts 404ing.
    private static readonly string Model =
        Environment.GetEnvironmentVariable("OPENROUTER_MODEL")
        ?? "meta-llama/llama-3.3-70b-instruct";

    public static bool IsOpenRouterAvailable => !string.IsNullOrEmpty(ApiKey);

    public static IChatClient Create(OllamaFixture ollamaFixture, string ollamaModel = "llama3.2:1b")
    {
        if (IsOpenRouterAvailable)
        {
            var openAiClient = new OpenAIClient(
                new ApiKeyCredential(ApiKey!),
                new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") });
            return openAiClient.GetChatClient(Model).AsIChatClient();
        }

        return ollamaFixture.CreateChatClient(ollamaModel);
    }
}
