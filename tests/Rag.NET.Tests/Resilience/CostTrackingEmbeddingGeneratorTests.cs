using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using NSubstitute;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Xunit;

namespace Rag.NET.Tests.Resilience;

public class CostTrackingEmbeddingGeneratorTests
{
    private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private static CostBudgetOptions Options(decimal dailyLimit = 10m, decimal embeddingPrice = 0m) => new()
    {
        DailyLimit = dailyLimit,
        EmbeddingPricePerMTokens = embeddingPrice,
    };

    private static IEmbeddingGenerator<string, Embedding<float>> RespondingInner(int count = 1)
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embeddings = new List<Embedding<float>>();
        for (int i = 0; i < count; i++)
        {
            embeddings.Add(new Embedding<float>(new float[] { i }));
        }

        inner.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(embeddings));
        return inner;
    }

    [Fact]
    public async Task GenerateAsync_EstimatesInputTokens_OutputZero_CostFromEmbeddingPrice()
    {
        string[] values = ["the quick brown fox", "jumps over the lazy dog"];
        var ledger = new FakeCostLedger();
        var sut = new CostTrackingEmbeddingGenerator(
            RespondingInner(2), ledger, Options(embeddingPrice: 0.02m));

        var result = await sut.GenerateAsync(values, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        var entry = Assert.Single(ledger.Recorded);
        long expectedTokens = s_tokenizer.CountTokens(values[0]) + s_tokenizer.CountTokens(values[1]);
        Assert.Equal(CostKind.Embedding, entry.Kind);
        Assert.Equal(expectedTokens, entry.InputTokens);
        Assert.Equal(0, entry.OutputTokens);
        Assert.Equal(expectedTokens / 1_000_000m * 0.02m, entry.Cost);
    }

    [Fact]
    public async Task GenerateAsync_BudgetExhausted_ThrowsAndInnerNeverCalled()
    {
        var ledger = new FakeCostLedger { DaySpend = 10m };
        var inner = RespondingInner();
        var sut = new CostTrackingEmbeddingGenerator(inner, ledger, Options(dailyLimit: 10m));

        var ex = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            sut.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CostWindow.Day, ex.Window);
        await inner.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_LedgerReadFails_CallProceedsUngated()
    {
        var ledger = new FakeCostLedger { ThrowOnRead = new IOException("disk full"), DaySpend = 999m };
        var sut = new CostTrackingEmbeddingGenerator(RespondingInner(), ledger, Options(dailyLimit: 1m));

        var result = await sut.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
    }

    [Fact]
    public async Task GenerateAsync_LedgerRecordFails_CallStillSucceeds()
    {
        var ledger = new FakeCostLedger { ThrowOnRecord = new IOException("disk full") };
        var sut = new CostTrackingEmbeddingGenerator(RespondingInner(), ledger, Options());

        var result = await sut.GenerateAsync(["x"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Empty(ledger.Recorded);
    }

    [Fact]
    public void GetService_AnswersForItsOwnType_ThenDelegates()
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var sentinel = new object();
        inner.GetService(typeof(string), "key").Returns(sentinel);
        var sut = new CostTrackingEmbeddingGenerator(inner, new FakeCostLedger(), Options());

        Assert.Same(sut, sut.GetService(typeof(CostTrackingEmbeddingGenerator)));
        Assert.Same(sentinel, sut.GetService(typeof(string), "key"));
    }

    [Fact]
    public void Dispose_DoesNotDisposeInner()
    {
        var inner = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

        new CostTrackingEmbeddingGenerator(inner, new FakeCostLedger(), Options()).Dispose();

        inner.DidNotReceive().Dispose();
    }
}
