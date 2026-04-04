using Microsoft.Extensions.AI;
using Testcontainers.Ollama;
using Xunit;

namespace Rag.NET.Testing;

public sealed class OllamaFixture : IAsyncLifetime
{
    private static readonly bool NeedsLocalGeneration =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));

    private readonly OllamaContainer _container = new OllamaBuilder("ollama/ollama:latest").Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await _container.ExecAsync(["ollama", "pull", "nomic-embed-text"]);

        if (NeedsLocalGeneration)
        {
            await _container.ExecAsync(["ollama", "pull", "llama3.2:1b"]);
        }
    }

    public async ValueTask DisposeAsync() =>
        await _container.DisposeAsync();

    public IChatClient CreateChatClient(string model) =>
        new OllamaChatClient(_container.GetBaseAddress(), model);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string model) =>
        new OllamaEmbeddingGenerator(_container.GetBaseAddress(), model);
}
