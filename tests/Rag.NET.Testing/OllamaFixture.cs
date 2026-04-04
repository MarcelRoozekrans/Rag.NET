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

        var embedResult = await _container.ExecAsync(["ollama", "pull", "nomic-embed-text"]);
        if (embedResult.ExitCode != 0)
            throw new InvalidOperationException(
                $"Failed to pull 'nomic-embed-text': exit {embedResult.ExitCode}\n{embedResult.Stderr}");

        if (NeedsLocalGeneration)
        {
            var genResult = await _container.ExecAsync(["ollama", "pull", "llama3.2:1b"]);
            if (genResult.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Failed to pull 'llama3.2:1b': exit {genResult.ExitCode}\n{genResult.Stderr}");
        }
    }

    public async ValueTask DisposeAsync() =>
        await _container.DisposeAsync();

    public IChatClient CreateChatClient(string model) =>
        new OllamaChatClient(_container.GetBaseAddress(), model);

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string model) =>
        new OllamaEmbeddingGenerator(_container.GetBaseAddress(), model);
}
