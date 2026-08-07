using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.PgVector;
using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.E2ETests;

/// <summary>
/// Reproduces the scenario behind
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/56">issue #56</see>: a personal
/// corpus that <i>does</i> contain the answer, queried with the reporter's own settings.
/// </summary>
[Collection("Ollama")]
public sealed class AddressRetrievalReproTests : IAsyncLifetime
{
    // Synthetic. Deliberately a real-looking Amsterdam address so the chunk reads like the
    // reporter's would, and deliberately nobody's.
    private const string Address = "Keizersgracht 123, 1015 CJ Amsterdam";

    private readonly OllamaFixture _ollama;
    private readonly PgVectorFixture _pgVector = new();
    private IRagPipeline _pipeline = null!;
    private ServiceProvider _sp = null!;
    private readonly List<string> _docIds = [];

    public AddressRetrievalReproTests(OllamaFixture ollama)
    {
        _ollama = ollama;
    }

    public async ValueTask InitializeAsync()
    {
        await _pgVector.InitializeAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            _ollama.CreateEmbeddingGenerator("nomic-embed-text"));
        services.AddSingleton(TestChatClientFactory.Create(_ollama));
        services.AddRagNet(rag => rag.UsePgVector(_pgVector.ConnectionString, vectorDimensions: 768));

        _sp = services.BuildServiceProvider();
        await ((PgVectorStore)_sp.GetRequiredService<IVectorStore>())
            .InitializeAsync(TestContext.Current.CancellationToken);
        _pipeline = _sp.GetRequiredService<IRagPipeline>();

        await IngestAsync("profile", $"""
            Personal profile.
            Name: Jan de Vries.
            Address: {Address}, Netherlands.
            Phone: +31 20 555 0134.
            """);

        await IngestAsync("expenses", """
            Expense policy. Submit receipts within 30 days. Reimbursement is paid to the
            bank account on file. Questions go to the finance email address listed on the
            intranet.
            """);

        await IngestAsync("meeting", """
            Meeting notes, 12 March. Discussed the Q2 roadmap, hiring plans, and the office
            move. Action items were assigned to the platform team.
            """);

        await IngestAsync("shipping", """
            Shipping information. Orders are dispatched within two business days. Delivery
            addresses cannot be changed once an order has shipped.
            """);

        await IngestAsync("onboarding", """
            Onboarding checklist. Set up your laptop, enrol in two-factor authentication,
            and confirm your contact details are correct in the HR system.
            """);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sp is not null)
        {
            foreach (var id in _docIds)
                await _pipeline.DeleteAsync(id, CancellationToken.None);
            await _sp.DisposeAsync();
        }

        await _pgVector.DisposeAsync();
    }

    /// <summary>
    /// The reported symptom does not reproduce when the corpus actually contains the answer:
    /// the address chunk is retrieved and the model uses it. This is the evidence that
    /// <c>SystemPrompt</c> and the engine were never the problem.
    /// </summary>
    /// <remarks>
    /// It is a near miss, which is the point worth guarding. Measured with
    /// <c>nomic-embed-text</c>, the profile chunk scores ~0.525 against the reporter's
    /// <c>MinScore = 0.5</c> — clearing the floor by ~0.025 — while a decoy reading "delivery
    /// addresses cannot be changed" scores ~0.640 and outranks it. A different embedding model,
    /// a longer document or a slightly different phrasing puts the real answer under the floor,
    /// and the caller then gets "there isn't enough information in the provided context" from a
    /// corpus that plainly contains it. The assertion here is only that it is retrieved at all;
    /// the exact scores are recorded as narrative, not asserted, because they are
    /// embedding-model specific.
    /// </remarks>
    [Fact]
    public async Task WhatIsMyAddress_WithReporterSettings_RetrievesTheAddressAndAnswersWithIt()
    {
        var response = await AskAsync(new RagOptions { TopK = 5, MinScore = 0.5 });

        Assert.NotEmpty(response.Sources);
        Assert.Contains(response.Sources, ContainsAddress);
        Assert.Contains("Keizersgracht", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dense-only retrieval ranks a semantic decoy above the document that literally contains the
    /// address; enabling hybrid search puts the right chunk first, because BM25 matches the
    /// query's "address" token directly.
    /// </summary>
    /// <remarks>
    /// Guards the recommendation rather than merely stating it. Note that <c>MinScore</c> means
    /// something different here: <c>EnsembleBehavior</c> passes it to the dense arm only, BM25
    /// hits bypass it entirely, and the returned <see cref="SearchResult.Score"/> is an RRF value
    /// (~0.016 in this corpus) that cannot meaningfully be compared against it.
    /// </remarks>
    [Fact]
    public async Task WhatIsMyAddress_WithHybridSearch_RanksTheAddressChunkFirst()
    {
        var response = await AskAsync(
            new RagOptions { TopK = 5, MinScore = 0.0, UseHybridSearch = true });

        Assert.NotEmpty(response.Sources);
        Assert.True(
            ContainsAddress(response.Sources[0]),
            "Expected the profile chunk to rank first under hybrid search. Got:\n"
                + Describe(response));
    }

    private async Task<RagResponse> AskAsync(RagOptions options) =>
        await _pipeline.AskAsync(
            "What is my address?", options, cancellationToken: TestContext.Current.CancellationToken);

    private static bool ContainsAddress(SearchResult result) =>
        (result.CompressedText ?? result.Chunk.Text).Contains(Address, StringComparison.Ordinal);

    private static string Describe(RagResponse response)
    {
        var sb = new StringBuilder();
        var rank = 1;
        foreach (var s in response.Sources)
        {
            var text = (s.CompressedText ?? s.Chunk.Text).ReplaceLineEndings(" ");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  #{rank++} score={s.Score:F4} :: {text}");
        }

        return sb.ToString();
    }

    private async Task IngestAsync(string name, string text)
    {
        var id = $"repro56-{name}-{Guid.CreateVersion7():N}";
        _docIds.Add(id);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var result = await _pipeline.IngestAsync(
            stream,
            new DocumentMetadata
            {
                DocumentId = new DocumentId(id),
                FileName = $"{name}.txt",
                ContentType = "text/plain",
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, $"IngestAsync failed for '{name}': {result}");
    }
}
