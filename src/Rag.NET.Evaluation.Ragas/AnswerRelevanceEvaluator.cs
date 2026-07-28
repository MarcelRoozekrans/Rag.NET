using System.Globalization;
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Ragas.Judging;
using Rag.NET.Models;

namespace Rag.NET.Evaluation.Ragas;

/// <summary>
/// Answer Relevance: mean cosine similarity between the original question and <c>n</c> synthetic
/// questions the predicted answer would answer (0–1, higher = more relevant), or <c>null</c> when
/// the sample cannot be scored. A noncommittal answer scores <c>0.0</c>.
/// </summary>
/// <remarks>
/// Three Phase 3.1 corrections. An evasive answer or refusal now scores zero, as published RAGAS
/// specifies, instead of scoring well for being topically close to the question. The synthetic
/// questions come from one call returning a JSON array, rather than <c>n</c> identical prompts
/// that at temperature 0 returned <c>n</c> identical questions and silently collapsed the mean to
/// a single sample. And the cosine, which ranges over [-1, 1], is clamped to the documented [0, 1]
/// instead of letting a negative similarity propagate into the aggregate.
/// </remarks>
public sealed class AnswerRelevanceEvaluator : IRagasMetric
{
    private const string NoncommittalPrompt =
        "Is the answer evasive or a refusal to answer (for example \"I don't know\", " +
        "\"the context does not say\")? Respond with exactly \"yes\" or \"no\" and nothing else.";

    private readonly RagasJudge _judge;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICostLedger? _costLedger;
    private readonly int _syntheticQuestionCount;
    private readonly decimal _pricePerEmbeddingToken;

    /// <summary>Creates an evaluator that judges with its own <see cref="IChatClient"/>.</summary>
    /// <param name="chatClient">The model asked to detect evasion and generate questions.</param>
    /// <param name="embeddingGenerator">Embeds the original and synthetic questions.</param>
    /// <param name="options">Run tuning; defaults are used when omitted.</param>
    /// <param name="costLedger">Optional ledger for the LLM spend this metric incurs.</param>
    public AnswerRelevanceEvaluator(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RagasOptions? options = null,
        ICostLedger? costLedger = null)
        : this(options ?? new RagasOptions(), chatClient, embeddingGenerator, costLedger)
    {
    }

    // Options first, so this overload differs from the public one by more than nullability
    // (CS0111). It exists only so the resolved options reach both the judge and this evaluator
    // without being built twice.
    private AnswerRelevanceEvaluator(
        RagasOptions options,
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ICostLedger? costLedger)
        : this(
            new RagasJudge(chatClient, options, costLedger),
            embeddingGenerator,
            options,
            costLedger)
    {
    }

    /// <summary>Creates an evaluator sharing a judge — and so a concurrency ceiling — with others.</summary>
    /// <param name="judge">The shared judge.</param>
    /// <param name="embeddingGenerator">Embeds the original and synthetic questions.</param>
    /// <param name="options">Run tuning: the question count and the embedding token price.</param>
    /// <param name="costLedger">
    /// Optional ledger for the embedding spend. The judge bills its own chat calls; this one is
    /// the only spend the metric incurs outside it, so the evaluator has to record it itself.
    /// </param>
    internal AnswerRelevanceEvaluator(
        RagasJudge judge,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        RagasOptions options,
        ICostLedger? costLedger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SyntheticQuestionCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "SyntheticQuestionCount must be at least 1.");

        _judge = judge;
        _embeddingGenerator = embeddingGenerator;
        _costLedger = costLedger;
        _syntheticQuestionCount = options.SyntheticQuestionCount;
        _pricePerEmbeddingToken = options.PricePerEmbeddingToken;
    }

    /// <inheritdoc />
    public bool RequiresGroundTruth => false;

    /// <inheritdoc />
    public async Task<double?> ScoreAsync(EvaluationSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        // Asked first, and answered before any embedding call: an evasive answer is scored zero
        // whatever it resembles, so embedding it would be spend with no effect on the result.
        var noncommittal = await _judge
            .ClassifyAsync(NoncommittalPrompt, sample.PredictedAnswer, cancellationToken)
            .ConfigureAwait(false);

        if (noncommittal == Verdict.Yes)
            return 0.0;

        var questions = await _judge
            .ExtractListAsync(GenerateQuestionsPrompt(), sample.PredictedAnswer, cancellationToken)
            .ConfigureAwait(false);

        if (!questions.Parsed || questions.Items.Count == 0)
            return null;

        return await SimilarityToAsync(sample.Question, questions.Items, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One call for all <c>n</c> questions, so they can differ from each other.</summary>
    private string GenerateQuestionsPrompt()
        => string.Format(
            CultureInfo.InvariantCulture,
            "Generate {0} different questions that the following answer responds to. " +
            "Output a JSON array of strings. No explanation.",
            _syntheticQuestionCount);

    private async Task<double?> SimilarityToAsync(
        string question, IReadOnlyList<string> syntheticQuestions, CancellationToken cancellationToken)
    {
        var texts = new List<string>(syntheticQuestions.Count + 1) { question };
        for (var i = 0; i < syntheticQuestions.Count; i++)
            texts.Add(syntheticQuestions[i]);

        var embeddings = await _embeddingGenerator
            .GenerateAsync(texts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Before the ragged-response guard below: the batch was billed whether or not its result
        // turns out to be usable, and a run's spend has to include the calls that went nowhere.
        await RecordEmbeddingCostAsync(embeddings, cancellationToken).ConfigureAwait(false);

        // A generator that returned fewer vectors than texts leaves nothing honest to average.
        if (embeddings.Count != texts.Count)
            return null;

        return RagasMath.ClampScore(MeanCosineSimilarity(embeddings));
    }

    /// <summary>
    /// Bills the embedding batch, the one call this metric makes that the judge does not.
    /// </summary>
    /// <remarks>
    /// Deliberately the same posture as the judge's chat path. A generator that reports no token
    /// count gets no entry, because recording zero tokens would state as fact that the batch was
    /// free; and a ledger that is down never fails an evaluation, because the batch has already
    /// been paid for and losing the score over a bookkeeping error is the worse outcome.
    /// <para>
    /// Only <see cref="CostEntry.InputTokens"/> is populated, and so only its absence suppresses
    /// the entry: an embedding API bills the text it was given, and its output is vectors rather
    /// than tokens.
    /// </para>
    /// </remarks>
    private async Task RecordEmbeddingCostAsync(
        GeneratedEmbeddings<Embedding<float>> embeddings, CancellationToken cancellationToken)
    {
        if (_costLedger is null || embeddings.Usage is not { InputTokenCount: { } inputTokens })
            return;

        var entry = new CostEntry
        {
            Kind = CostKind.Embedding,
            InputTokens = inputTokens,
            Cost = inputTokens * _pricePerEmbeddingToken,
        };

        try
        {
            await _costLedger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Billing visibility must never break an evaluation run.
        }
    }

    private static double MeanCosineSimilarity(GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        var original = embeddings[0].Vector.Span;
        var sum = 0.0;
        for (var i = 1; i < embeddings.Count; i++)
            sum += TensorPrimitives.CosineSimilarity(embeddings[i].Vector.Span, original);

        return sum / (embeddings.Count - 1);
    }
}
