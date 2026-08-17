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

    // The default above is text-only, so it cannot serve an image test: passing a DataContent to a
    // non-multimodal model does not fail loudly, it produces a confident description of nothing.
    // Kept separate for the same reason the text default is pinned here rather than at call sites —
    // when a model is delisted there is one line to change, and the comment above records what
    // happens when that line is spread around instead.
    //
    // qwen/qwen3.7-flash, chosen over the several :free vision models on the catalogue on purpose:
    // probed 2026-08-17, google/gemma-4-31b-it:free answered HTTP 429 "temporarily rate-limited
    // upstream" on the first call, which is a flaky suite rather than a cheap one. This model read
    // the fixture correctly for $0.00018 a call, so the free tier buys nothing worth its
    // unreliability.
    private static readonly string VisionModel =
        Environment.GetEnvironmentVariable("OPENROUTER_VISION_MODEL")
        ?? "qwen/qwen3.7-flash";

    public static bool IsOpenRouterAvailable => !string.IsNullOrEmpty(ApiKey);

    /// <summary>Gets the vision model identifier <see cref="CreateVisionClient"/> will use.</summary>
    /// <remarks>Exposed so a test can report which model produced a description it asserts on.</remarks>
    public static string VisionModelId => VisionModel;

    /// <summary>
    /// Creates an <see cref="IChatClient"/> backed by a vision-capable OpenRouter model.
    /// </summary>
    /// <remarks>
    /// No Ollama fallback, deliberately, and it is worth saying why rather than leaving the
    /// asymmetry with <see cref="Create"/> unexplained: the fallback there is a 1B text model that
    /// pulls in seconds, whereas the smallest usable local vision model is a multi-gigabyte pull on
    /// a cold container. A caller without a key should skip and say so, not silently spend ten
    /// minutes downloading one.
    /// </remarks>
    /// <returns>The client.</returns>
    /// <exception cref="InvalidOperationException">No API key is configured.</exception>
    public static IChatClient CreateVisionClient()
    {
        if (!IsOpenRouterAvailable)
        {
            throw new InvalidOperationException(
                "OPENROUTER_API_KEY is not set. Check IsOpenRouterAvailable and skip instead of " +
                "calling this: there is no local fallback for a vision model.");
        }

        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(ApiKey!),
            new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") });

        return openAiClient.GetChatClient(VisionModel).AsIChatClient();
    }

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
