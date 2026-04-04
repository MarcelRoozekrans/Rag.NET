using System.Reflection;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PgVector;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.E2ETests;

[Collection("Ollama")]
public sealed class FullPipelineTests : IAsyncLifetime
{
    private readonly OllamaFixture _ollama;
    private readonly PgVectorFixture _pgVector = new();
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;

    public FullPipelineTests(OllamaFixture ollama)
    {
        _ollama = ollama;
    }

    public async ValueTask InitializeAsync()
    {
        await _pgVector.InitializeAsync();

        var chatClient = TestChatClientFactory.Create(_ollama);
        var embeddingGenerator = _ollama.CreateEmbeddingGenerator("nomic-embed-text");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingGenerator);
        services.AddSingleton<IChatClient>(chatClient);
        services.AddRagNet(rag => rag
            .UsePgVector(_pgVector.ConnectionString, vectorDimensions: 768)
            .UseDispatchingAnswerEngine());

        _sp = services.BuildServiceProvider();

        // Initialise the vector store schema
        var store = (PgVectorStore)_sp.GetRequiredService<IVectorStore>();
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        await IngestDocumentsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
            await _sp.DisposeAsync();

        await _pgVector.DisposeAsync();
    }

    [Fact]
    public async Task FullPipeline_Chat_AnswersQuestionAboutEiffelTower()
    {
        var response = await _pipeline.AskAsync(
            "Where is the Eiffel Tower located?",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Answer), "Expected a non-empty answer.");
        Assert.Contains("Paris", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullPipeline_MapReduce_AnswersQuestion()
    {
        var options = new RagOptions { SynthesisStrategy = SynthesisStrategy.MapReduce };

        var response = await _pipeline.AskAsync(
            "What programming language was created by Guido van Rossum?",
            options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Answer), "Expected a non-empty answer.");
        Assert.Contains("Python", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullPipeline_Refine_AnswersQuestion()
    {
        var options = new RagOptions { SynthesisStrategy = SynthesisStrategy.Refine };

        var response = await _pipeline.AskAsync(
            "Who designed the Eiffel Tower?",
            options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Answer), "Expected a non-empty answer.");
        Assert.Contains("Eiffel", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private async Task IngestDocumentsAsync()
    {
        var assembly = typeof(FullPipelineTests).Assembly;
        var resources = new[]
        {
            ("doc1", "doc1.txt"),
            ("doc2", "doc2.txt"),
            ("doc3", "doc3.txt"),
        };

        foreach (var (docId, fileName) in resources)
        {
            var resourceName = $"Rag.NET.E2ETests.Resources.{fileName}";
            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            var result = await _pipeline.IngestAsync(
                stream,
                new DocumentMetadata
                {
                    DocumentId = new DocumentId(docId),
                    FileName = fileName,
                    ContentType = "text/plain",
                },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, $"IngestAsync failed for '{fileName}': {result}");
        }
    }
}
