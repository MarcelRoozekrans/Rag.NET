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
    private readonly string _doc1Id = $"e2e-{Guid.CreateVersion7():N}";
    private readonly string _doc2Id = $"e2e-{Guid.CreateVersion7():N}";
    private readonly string _doc3Id = $"e2e-{Guid.CreateVersion7():N}";

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
        {
            await _pipeline.DeleteAsync(_doc1Id, CancellationToken.None);
            await _pipeline.DeleteAsync(_doc2Id, CancellationToken.None);
            await _pipeline.DeleteAsync(_doc3Id, CancellationToken.None);
            await _sp.DisposeAsync();
        }

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

    [Fact]
    public async Task FullPipeline_Dispatching_AnswersQuestion()
    {
        var options = new RagOptions { SynthesisStrategy = SynthesisStrategy.Default };

        var response = await _pipeline.AskAsync(
            "What programming language was created by Guido van Rossum?",
            options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(response.Answer), "Expected a non-empty answer.");
        Assert.Contains("Python", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scenario <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/56">issue #56</see>
    /// actually reported: documents ingested, sources retrieved, and a custom
    /// <see cref="RagOptions.SystemPrompt"/> expected to hold.
    /// </summary>
    /// <remarks>
    /// <c>SystemPromptE2ETests</c> covers the same contract against an empty store, which leaves the
    /// context block empty and the <c>[Source N]</c> labels absent entirely — so it cannot show that
    /// a custom prompt survives real retrieved context. That is the case the reporter hit, and it is
    /// this one. All three assertions are load-bearing: without the source check the marker could
    /// pass with retrieval returning nothing, which is precisely the hole this test exists to close.
    /// <para>
    /// Requires OpenRouter rather than the <c>llama3.2:1b</c> fallback, which is measurably not
    /// capable of this once a context block is competing for its attention: instructed to append a
    /// literal marker it either emitted the marker <i>alone</i> with no answer, or filled the
    /// brackets in as a template (<c>&lt;&lt;Paris, France&gt;&gt;</c>) — the latter in 3 of 3 runs.
    /// The assertion is not weakened to accommodate that; the model is simply too small, and the
    /// nightly LLM tier supplies <c>OPENROUTER_API_KEY</c> so this runs there for real. The
    /// source-free <c>SystemPromptE2ETests</c> passes on both backends and keeps the contract
    /// covered for contributors running locally without a key.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task FullPipeline_CustomSystemPrompt_HoldsWhenSourcesAreRetrieved()
    {
        Assert.SkipUnless(
            TestChatClientFactory.IsOpenRouterAvailable,
            "Needs OPENROUTER_API_KEY: the llama3.2:1b fallback cannot follow a marker instruction "
            + "alongside retrieved context.");

        const string marker = "<<RAGNET>>";
        var options = new RagOptions
        {
            SystemPrompt =
                "Answer the question using the context, in one short sentence. " +
                $"After that sentence, append {marker}",
        };

        var response = await _pipeline.AskAsync(
            "Where is the Eiffel Tower located?",
            options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Sources);
        Assert.Contains("Paris", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(marker, response.Answer, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private async Task IngestDocumentsAsync()
    {
        var assembly = typeof(FullPipelineTests).Assembly;
        var resources = new[]
        {
            (_doc1Id, "doc1.txt"),
            (_doc2Id, "doc2.txt"),
            (_doc3Id, "doc3.txt"),
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
