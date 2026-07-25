using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Resilience;
using Rag.NET.Storage;
using Rag.NET.Tests.Resilience;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public sealed class UseCostBudgetingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-costbudget-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static IChatClient RespondingChatClient(string text)
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return client;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> RespondingEmbeddingGenerator()
    {
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        generator.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[] { 1f })]));
        return generator;
    }

    // ── Decoration ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UseCostBudgeting_DecoratesBothRegisteredSurfaces_CallsFlowThrough()
    {
        var chatInner = RespondingChatClient("ok");
        var embeddingInner = RespondingEmbeddingGenerator();
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(chatInner);
                rag.Services.AddSingleton(embeddingInner);
                rag.Services.AddSingleton<ICostLedger>(new InMemoryCostLedger());
                rag.UseCostBudgeting(o => o.DailyLimit = 10m);
            })
            .BuildServiceProvider();

        var chat = Assert.IsType<CostTrackingChatClient>(sp.GetRequiredService<IChatClient>());
        Assert.IsType<CostTrackingEmbeddingGenerator>(sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ok", response.Text);
        await chatInner.Received(1).GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UseCostBudgeting_ChatOnlyRegistration_DecoratesChatWithoutRequiringEmbeddings()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(RespondingChatClient("ok"));
                rag.Services.AddSingleton<ICostLedger>(new InMemoryCostLedger());
                rag.UseCostBudgeting(o => o.MonthlyLimit = 100m);
            })
            .BuildServiceProvider();

        Assert.IsType<CostTrackingChatClient>(sp.GetRequiredService<IChatClient>());
        Assert.Null(sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    // ── Ledger registration ──────────────────────────────────────────────────

    [Fact]
    public void UseCostBudgeting_NoCustomLedger_RegistersSqliteLedgerAtConfiguredPath()
    {
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(RespondingChatClient("ok"));
                rag.UseCostBudgeting(o =>
                {
                    o.DailyLimit = 10m;
                    o.DatabasePath = _dbPath;
                });
            })
            .BuildServiceProvider();

        Assert.IsType<SqliteCostLedger>(sp.GetRequiredService<ICostLedger>());
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void UseCostBudgeting_CustomLedgerRegisteredFirst_Wins()
    {
        var custom = new InMemoryCostLedger();
        var sp = new ServiceCollection()
            .AddRagNet(rag =>
            {
                rag.Services.AddSingleton(RespondingChatClient("ok"));
                rag.Services.AddSingleton<ICostLedger>(custom);
                rag.UseCostBudgeting(o => o.DailyLimit = 10m);
            })
            .BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<ICostLedger>());
    }

    // ── Idempotence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UseCostBudgeting_CalledTwice_DoesNotStackDecorators()
    {
        var log = new List<string>();
        var ledger = new FakeCostLedger(log) { DaySpend = 1m };
        var services = new ServiceCollection();
        services.AddRagNet(rag =>
        {
            rag.Services.AddSingleton(RespondingChatClient("ok"));
            rag.Services.AddSingleton<ICostLedger>(ledger);
            rag.UseCostBudgeting(o => o.DailyLimit = 10m);
            rag.UseCostBudgeting(o => o.DailyLimit = 5m); // ignored: first configuration wins
        });

        Assert.Equal(1, services.Count(d =>
            d.IsKeyedService && Equals(d.ServiceKey, RagBuilderExtensions.CostBudgetingAppliedKey)));

        using var sp = services.BuildServiceProvider();
        var chat = Assert.IsType<CostTrackingChatClient>(sp.GetRequiredService<IChatClient>());
        // A stacked decoration would gate (read the ledger) twice per call.
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, log.Count(entry => entry.StartsWith("ledger-read", StringComparison.Ordinal)));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void UseCostBudgeting_NullConfigure_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(null!)));
    }

    [Fact]
    public void UseCostBudgeting_NoLimitConfigured_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(o => o.InputPricePerMTokens = 3m)));

        Assert.Contains("at least one limit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UseCostBudgeting_NegativePrice_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(o =>
            {
                o.DailyLimit = 10m;
                o.OutputPricePerMTokens = -1m;
            })));
    }

    [Fact]
    public void UseCostBudgeting_NegativeLimit_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(o => o.DailyLimit = -1m)));
    }

    [Fact]
    public void UseCostBudgeting_BlankDatabasePath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(o =>
            {
                o.DailyLimit = 10m;
                o.DatabasePath = " ";
            })));
    }

    [Fact]
    public void UseCostBudgeting_NoSurfaceRegistered_ThrowsActionable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRagNet(rag => rag.UseCostBudgeting(o => o.DailyLimit = 10m)));

        Assert.Contains("before calling UseCostBudgeting", ex.Message, StringComparison.Ordinal);
    }
}
