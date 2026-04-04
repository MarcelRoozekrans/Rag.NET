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

    private static readonly string Model =
        Environment.GetEnvironmentVariable("OPENROUTER_MODEL")
        ?? "nvidia/llama-3.1-nemotron-70b-instruct";

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
