using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Tests.Evaluation;

/// <summary>
/// End-to-end test: EvaluationDatasetBuilder → fill PredictedAnswer → RagasEvaluationSuite.
/// Validates that the full synthetic-dataset-to-RAGAS-report pipeline composes correctly.
/// </summary>
public class RagasEndToEndTests
{
    private static IRagDataManager MakeTwoChunkDataManager()
    {
        var dataManager = Substitute.For<IRagDataManager>();
        var docId = new DocumentId("doc-1");
        dataManager.GetDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DocumentSummary>
            {
                new() { DocumentId = docId, FileName = "test.txt", ChunkCount = 2, IngestedAt = DateTimeOffset.UnixEpoch },
            });
        dataManager.GetChunksAsync(docId.Value, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TextChunk>)new List<TextChunk>
            {
                new() { Text = "The sky is blue.", DocumentId = docId, ChunkIndex = 0 },
                new() { Text = "Water boils at 100°C.", DocumentId = docId, ChunkIndex = 1 },
            });
        return dataManager;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> MakeIdentityEmbedder()
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IEnumerable<string>>().ToList();
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in inputs)
                    result.Add(new Embedding<float>(new float[] { 1f, 0f }));
                return Task.FromResult(result);
            });
        return embedder;
    }

    [Fact]
    public async Task SyntheticDataset_ThenRagasSuite_ProducesReport()
    {
        var dataManager = MakeTwoChunkDataManager();

        // Dataset generation is sequential, so canned replies in order are safe here.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "What color is the sky?")),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "At what temperature does water boil?")));

        // Scoring runs its metrics concurrently, so the judge gets a client that routes on
        // content instead: not evasive, three synthetic questions, and no claims to verify.
        var judgeClient = Substitute.For<IChatClient>();
        judgeClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = string.Join('\n', callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Text));
                var reply = prompt switch
                {
                    _ when prompt.Contains("evasive", StringComparison.Ordinal) => "no",
                    _ when prompt.Contains("different questions", StringComparison.Ordinal) => "[\"Q1?\",\"Q2?\",\"Q3?\"]",
                    _ => "[]",
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
            });

        var embedder = MakeIdentityEmbedder();

        // Step 1: Generate synthetic dataset
        var builder = new EvaluationDatasetBuilder(dataManager, chatClient);
        var samples = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, samples.Count);

        // Step 2: Simulate pipeline filling PredictedAnswer
        var evaluated = samples
            .Select(s => s with { PredictedAnswer = "A simulated answer.", SourceChunks = s.SourceChunks })
            .ToList();

        // Step 3: Score with RAGAS (Faithfulness + AnswerRelevance)
        var suite = new RagasEvaluationSuiteBuilder(judgeClient, embedder)
            .AddFaithfulness()
            .AddAnswerRelevance()
            .Build();

        var report = await suite.EvaluateAsync(evaluated, TestContext.Current.CancellationToken);

        Assert.NotNull(report.Faithfulness);
        Assert.NotNull(report.AnswerRelevance);
        Assert.Null(report.ContextPrecision);
        Assert.Null(report.ContextRecall);
        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 2);
        Assert.Equal(1.0, report.AnswerRelevance!.Value, precision: 2);
        Assert.Equal(1.0, report.OverallScore!.Value, precision: 2);
    }
}
