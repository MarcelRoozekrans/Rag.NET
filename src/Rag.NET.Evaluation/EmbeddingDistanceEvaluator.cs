using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation;

/// <summary>
/// Evaluates RAG answer quality by comparing cosine similarity between
/// embeddings of predicted and reference answers.
/// Score of 1.0 = identical semantic content; 0.0 = completely unrelated.
/// </summary>
public sealed class EmbeddingDistanceEvaluator(
    IEmbeddingGenerator<string, Embedding<float>> embedder) : IRagEvaluator
{
    public async Task<EvaluationResult> EvaluateAsync(
        IReadOnlyList<EvaluationSample> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        var predictedTexts = samples.Select(s => s.PredictedAnswer).ToList();
        var referenceTexts = samples.Select(s => s.ReferenceAnswer).ToList();

        var predictedEmbeddings = await embedder.GenerateAsync(predictedTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
        var referenceEmbeddings = await embedder.GenerateAsync(referenceTexts, cancellationToken: cancellationToken).ConfigureAwait(false);

        var scores = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            scores[i] = CosineSimilarity(predictedEmbeddings[i].Vector, referenceEmbeddings[i].Vector);
        }

        var meanScore = scores.Average();
        return new EvaluationResult(meanScore, scores);
    }

    private static double CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var spanA = a.Span;
        var spanB = b.Span;

        if (spanA.Length != spanB.Length)
            return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < spanA.Length; i++)
        {
            dot += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        double denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0.0 ? 0.0 : dot / denom;
    }
}
