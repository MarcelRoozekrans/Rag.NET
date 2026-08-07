using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;
using Rag.NET.Storage;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.E2ETests;

/// <summary>
/// Proves <see cref="RagOptions.SystemPrompt"/> reaches a real chat provider and changes its
/// output (<see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/56">issue #56</see>).
/// </summary>
/// <remarks>
/// No vector store infrastructure is needed to prove this: the assertion is about the system
/// prompt surviving <c>options → pipeline → engine → provider → output</c>, not about retrieval
/// quality, so an empty <see cref="InMemoryVectorStore"/> stands in for <c>PgVectorFixture</c> —
/// no container, no ingestion, no schema init. <see cref="OllamaFixture"/> is still required
/// because query embedding runs regardless of store, and <see cref="TestChatClientFactory"/>
/// prefers a real OpenRouter-backed <see cref="IChatClient"/> over the fixture's local model
/// whenever <c>OPENROUTER_API_KEY</c> is set.
/// </remarks>
[Collection("Ollama")]
public sealed class SystemPromptE2ETests : IAsyncLifetime
{
    private const string Marker = "<<RAGNET>>";

    private readonly OllamaFixture _ollama;
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;

    public SystemPromptE2ETests(OllamaFixture ollama)
    {
        _ollama = ollama;
    }

    public ValueTask InitializeAsync()
    {
        var chatClient = TestChatClientFactory.Create(_ollama);
        var embeddingGenerator = _ollama.CreateEmbeddingGenerator("nomic-embed-text");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingGenerator);
        services.AddSingleton<IChatClient>(chatClient);
        services.AddSingleton<IVectorStore>(_ => new InMemoryVectorStore());
        services.AddRagNet();

        _sp = services.BuildServiceProvider();
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
            await _sp.DisposeAsync();
    }

    [Fact]
    public async Task AskAsync_CustomSystemPrompt_MarkerAppearsInRealProviderResponse()
    {
        var options = new RagOptions
        {
            SystemPrompt =
                $"Answer in one short sentence, then write {Marker} at the end. " +
                $"Every reply must end with {Marker}",
        };

        var response = await _pipeline.AskAsync(
            "What is the capital of France?",
            options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Answer), "Expected a non-empty answer.");
        Assert.Contains(Marker, response.Answer, StringComparison.Ordinal);
    }
}
