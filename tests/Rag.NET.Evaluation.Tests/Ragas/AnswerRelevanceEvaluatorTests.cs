using Microsoft.Extensions.AI;
using Rag.NET.Evaluation.Ragas;
using Rag.NET.Evaluation.Ragas.Judging;
using Xunit;

namespace Rag.NET.Evaluation.Tests.Ragas;

public sealed class AnswerRelevanceEvaluatorTests
{
    private const string Questions = """["Q one?","Q two?","Q three?"]""";

    private static AnswerRelevanceEvaluator Evaluator(
        IChatClient client,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        int questionCount = 3)
        => new(new RagasJudge(client, new RagasOptions()), embeddings, questionCount);

    private static EvaluationSample Sample()
        => new("What is X?", "X is Y.", "X is Y.", ["context"]);

    /// <summary>Answers "no" to the evasion question and returns the question array otherwise.</summary>
    private static RoutingChatClient Cooperative(string questions = Questions)
        => new([("evasive", "no"), ("different questions", questions)], fallback: questions);

    [Fact]
    public async Task ScoreAsync_EvasiveAnswer_ScoresZeroWithoutEmbeddingAnything()
    {
        var client = new RoutingChatClient([("evasive", "yes")], fallback: Questions);
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        var score = await Evaluator(client, embeddings).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        // Published RAGAS scores a noncommittal answer zero; before Phase 3.1 there was no such
        // check, so an evasive answer that stayed on topic scored well.
        Assert.Equal(0.0, score!.Value, precision: 10);
        Assert.Equal(0, embeddings.CallCount);
        Assert.Single(client.Prompts);
    }

    [Fact]
    public async Task ScoreAsync_IdenticalEmbeddings_ScoresOne()
    {
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        var score = await Evaluator(Cooperative(), embeddings)
            .ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_OrthogonalEmbeddings_ScoresZero()
    {
        var embeddings = new StubEmbeddingGenerator(
            text => text.Contains("What is X?", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f]);

        var score = await Evaluator(Cooperative(), embeddings)
            .ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_OpposedEmbeddings_IsClampedToZeroNotNegative()
    {
        // Cosine ranges over [-1, 1]; the documented score range is [0, 1]. Before Phase 3.1 a
        // negative similarity propagated into the mean and then into OverallScore.
        var embeddings = new StubEmbeddingGenerator(
            text => text.Contains("What is X?", StringComparison.Ordinal) ? [1f, 0f] : [-1f, 0f]);

        var score = await Evaluator(Cooperative(), embeddings)
            .ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(0.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_GeneratesTheQuestionsInOneCallNotOnePerQuestion()
    {
        var client = Cooperative();
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        await Evaluator(client, embeddings).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        // One evasion check plus one generation call. Before Phase 3.1 this was n identical
        // prompts, which at temperature 0 returned n identical questions and collapsed the mean
        // to a single sample while reporting as if it had averaged three.
        Assert.Equal(2, client.CallCount);
        Assert.Contains(
            client.Prompts,
            prompt => prompt.Contains("Generate 3 different questions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScoreAsync_EmbedsTheOriginalQuestionAndEveryDistinctSyntheticQuestion()
    {
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        await Evaluator(Cooperative(), embeddings).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(["What is X?", "Q one?", "Q two?", "Q three?"], embeddings.Inputs);
    }

    [Fact]
    public async Task ScoreAsync_WhenQuestionGenerationFails_IsNotScoreable()
    {
        var client = new RoutingChatClient([("evasive", "no")], fallback: "Here are some questions:");
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        var score = await Evaluator(client, embeddings).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
        Assert.Equal(0, embeddings.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_WhenNoQuestionsAreGenerated_IsNotScoreable()
    {
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        var score = await Evaluator(Cooperative(questions: "[]"), embeddings)
            .ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
        Assert.Equal(0, embeddings.CallCount);
    }

    [Fact]
    public async Task ScoreAsync_UnreadableEvasionVerdict_DoesNotShortCircuitToZero()
    {
        // Only an explicit "yes" means evasive. An unreadable verdict is not a verdict, so
        // scoring continues rather than declaring the answer a refusal.
        var client = new RoutingChatClient([("evasive", "hard to say")], fallback: Questions);
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);

        var score = await Evaluator(client, embeddings).ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
    }

    [Fact]
    public async Task ScoreAsync_WhenTheGeneratorReturnsTooFewVectors_IsNotScoreable()
    {
        var embeddings = new StubEmbeddingGenerator([1f, 0f]) { TruncateTo = 2 };

        var score = await Evaluator(Cooperative(), embeddings)
            .ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Null(score);
    }

    [Fact]
    public void Constructor_ZeroQuestions_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Evaluator(Cooperative(), new StubEmbeddingGenerator([1f, 0f]), questionCount: 0));

        Assert.Equal("syntheticQuestionCount", exception.ParamName);
    }

    [Fact]
    public async Task ScoreAsync_ViaThePublicChatClientConstructor_UsesTheOptionsQuestionCount()
    {
        var client = Cooperative("""["only one?"]""");
        var embeddings = new StubEmbeddingGenerator([1f, 0f]);
        var evaluator = new AnswerRelevanceEvaluator(
            client, embeddings, new RagasOptions { SyntheticQuestionCount = 1 });

        var score = await evaluator.ScoreAsync(Sample(), TestContext.Current.CancellationToken);

        Assert.Equal(1.0, score!.Value, precision: 10);
        Assert.False(evaluator.RequiresGroundTruth);
        Assert.Contains(
            client.Prompts,
            prompt => prompt.Contains("Generate 1 different questions", StringComparison.Ordinal));
    }
}
