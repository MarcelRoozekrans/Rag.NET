using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Rag.NET.Evaluation;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Answer Relevance: mean cosine similarity between embeddings of n=3 synthetic questions
/// (generated from the predicted answer) and the embedding of the original question.
/// Score = mean cosine similarity (0–1, higher = more relevant answer).
/// </summary>
public sealed class AnswerRelevanceEvaluator(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    int syntheticQuestionCount = 3) : IRagasMetric
{
    private readonly int _syntheticQuestionCount = syntheticQuestionCount >= 1
        ? syntheticQuestionCount
        : throw new ArgumentOutOfRangeException(nameof(syntheticQuestionCount), "Must be at least 1.");

    public bool RequiresGroundTruth => false;

    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        // Generate n synthetic questions from the predicted answer (concurrently)
        var questionTasks = Enumerable.Range(0, _syntheticQuestionCount)
            .Select(_ => GenerateSyntheticQuestionAsync(sample.PredictedAnswer, cancellationToken));
        var syntheticQuestions = await Task.WhenAll(questionTasks).ConfigureAwait(false);

        // Embed original question + all synthetic questions in one batch
        var allTexts = new[] { sample.Question }.Concat(syntheticQuestions).ToList();
        var embeddings = await embeddingGenerator
            .GenerateAsync(allTexts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ComputeMeanCosineSimilarity(embeddings, _syntheticQuestionCount);
    }

    private static double ComputeMeanCosineSimilarity(GeneratedEmbeddings<Embedding<float>> embeddings, int syntheticQuestionCount)
    {
        var originalEmbedding = embeddings[0].Vector.Span;
        var similarities = new double[syntheticQuestionCount];
        for (var i = 0; i < syntheticQuestionCount; i++)
            similarities[i] = TensorPrimitives.CosineSimilarity(
                embeddings[i + 1].Vector.Span, originalEmbedding);
        return similarities.Average();
    }

    private async Task<string> GenerateSyntheticQuestionAsync(string answer, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Generate a single question that the following answer is responding to. " +
                "Output only the question, no explanation."),
            new(ChatRole.User, answer),
        };
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }
}
