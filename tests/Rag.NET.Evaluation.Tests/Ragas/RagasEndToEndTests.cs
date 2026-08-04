using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>
/// End-to-end: <see cref="EvaluationDatasetBuilder"/> → fill <c>PredictedAnswer</c> → RAGAS
/// suite. Pins that the synthetic-dataset-to-report pipeline composes at all — the one thing no
/// per-component suite here can show. Migrated from <c>tests/Rag.NET.Tests/Evaluation</c> when
/// that duplicate suite was deleted (Phase 4.1).
/// </summary>
public sealed class RagasEndToEndTests
{
    private static IRagDataManager TwoChunkDataManager()
    {
        var dataManager = Substitute.For<IRagDataManager>();
        var docId = new DocumentId("doc-1");
        dataManager.GetDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DocumentSummary>
            {
                new()
                {
                    DocumentId = docId,
                    FileName = "test.txt",
                    ChunkCount = 2,
                    IngestedAt = DateTimeOffset.UnixEpoch,
                },
            });
        dataManager.GetChunksAsync(docId.Value, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TextChunk>)new List<TextChunk>
            {
                new() { Text = "The sky is blue.", DocumentId = docId, ChunkIndex = 0 },
                new() { Text = "Water boils at 100°C.", DocumentId = docId, ChunkIndex = 1 },
            });
        return dataManager;
    }

    [Fact]
    public async Task SyntheticDataset_ThenRagasSuite_ProducesReport()
    {
        // Generation routes on the chunk text its prompt embeds; the empty fallback makes any
        // unexpected prompt produce an empty question, which the builder drops and the sample
        // count below then reports loudly.
        var generationClient = new RoutingChatClient(
        [
            ("sky is blue", "What color is the sky?"),
            ("Water boils", "At what temperature does water boil?"),
        ],
            fallback: string.Empty);

        // The judge: not evasive, three synthetic questions, and an answer asserting no claims.
        var judgeClient = new RoutingChatClient(
        [
            ("evasive", "no"),
            ("different questions", """["Q1?","Q2?","Q3?"]"""),
        ],
            fallback: "[]");

        // Step 1: generate the synthetic dataset.
        var builder = new EvaluationDatasetBuilder(TwoChunkDataManager(), generationClient);
        var dataset = await builder.BuildAsync(
            new EvaluationDatasetBuilderOptions { SampleCount = 2, Mode = DatasetGenerationMode.QuestionOnly },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, dataset.Samples.Count);
        Assert.Equal(2, dataset.Requested);
        Assert.Empty(dataset.Skipped);

        // Step 2: simulate the pipeline filling PredictedAnswer.
        var evaluated = new List<EvaluationSample>(dataset.Samples.Count);
        for (var i = 0; i < dataset.Samples.Count; i++)
            evaluated.Add(dataset.Samples[i] with { PredictedAnswer = "A simulated answer." });

        // Step 3: score with RAGAS.
        var suite = new RagasEvaluationSuiteBuilder(judgeClient, new StubEmbeddingGenerator([1f, 0f]))
            .AddFaithfulness()
            .AddAnswerRelevance()
            .Build();

        var report = await suite.EvaluateAsync(evaluated, TestContext.Current.CancellationToken);

        Assert.Equal(1.0, report.Faithfulness!.Value, precision: 10);
        Assert.Equal(1.0, report.AnswerRelevance!.Value, precision: 10);
        Assert.Null(report.ContextPrecision);
        Assert.Null(report.ContextRecall);
        Assert.Equal(1.0, report.OverallScore!.Value, precision: 10);
    }
}
