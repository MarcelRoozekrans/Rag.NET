using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Internal;
using Rag.NET.Models;

namespace Rag.NET.Evaluation;

/// <summary>
/// Generates synthetic evaluation samples from an existing document corpus.
/// Samples random chunks from <see cref="IRagDataManager"/> and uses an LLM
/// to generate a question (and optionally a reference answer) per chunk.
/// </summary>
/// <remarks>
/// Set <see cref="EvaluationDatasetBuilderOptions.Seed"/> to make the sampling repeatable — read
/// its documentation for what that does and does not guarantee. The corpus is streamed through a
/// reservoir rather than materialised, so a build holds the sample, not the corpus.
/// </remarks>
public sealed class EvaluationDatasetBuilder(
    IRagDataManager dataManager,
    IChatClient chatClient)
{
    private readonly IRagDataManager _dataManager = dataManager
        ?? throw new ArgumentNullException(nameof(dataManager));
    private readonly IChatClient _chatClient = chatClient
        ?? throw new ArgumentNullException(nameof(chatClient));

    /// <summary>
    /// Builds a synthetic evaluation dataset by sampling chunks from the document corpus
    /// and generating questions (and optionally reference answers) via LLM.
    /// </summary>
    /// <param name="options">Controls sample count and generation mode. Defaults to <see cref="EvaluationDatasetBuilderOptions"/> if null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The generated samples together with how many chunks were sent for generation, so a caller
    /// who asks for 50 and receives fewer can see that from the result rather than infer it.
    /// </returns>
    public async Task<EvaluationDataset> BuildAsync(
        EvaluationDatasetBuilderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationDatasetBuilderOptions();
        if (options.SampleCount <= 0)
            return new EvaluationDataset();

        var sampled = await SampleChunksAsync(options.SampleCount, options.Seed, cancellationToken)
            .ConfigureAwait(false);

        // Generate samples concurrently
        // Indexed rather than foreach: HLQ012 objects to enumerating a List<T> directly, and the
        // sampling loop below is written the same way.
        var tasks = new List<Task<GenerationResult>>(sampled.Count);
        for (var i = 0; i < sampled.Count; i++)
            tasks.Add(GenerateSampleAsync(sampled[i], options.Mode, cancellationToken));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return Collect(results, sampled.Count);
    }

    /// <summary>Splits the generation results into the samples and the account of what was dropped.</summary>
    /// <remarks>
    /// A reason that never occurred is left out of <see cref="EvaluationDataset.Skipped"/> rather
    /// than recorded as zero, so a caller can check whether the dictionary is empty to know whether
    /// anything was lost.
    /// </remarks>
    private static EvaluationDataset Collect(GenerationResult[] results, int requested)
    {
        var samples = new List<EvaluationSample>(results.Length);
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            if (result.Sample is { } sample)
                samples.Add(sample);
            else if (result.SkipReason is { } reason)
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        }

        return new EvaluationDataset
        {
            Samples = samples,
            Requested = requested,
            Skipped = skipped,
        };
    }

    /// <summary>
    /// Draws up to <paramref name="sampleCount"/> chunks uniformly from the corpus, holding only
    /// the sample.
    /// </summary>
    /// <remarks>
    /// Each document's chunks are offered to the reservoir as they arrive and then dropped, so
    /// memory is proportional to <paramref name="sampleCount"/> rather than to the corpus. This
    /// replaces accumulating every chunk of every document into one list and sorting it by a random
    /// key — which read and held a 100k-document corpus in order to pick five chunks out of it, and
    /// which, being unseeded, could not draw the same five twice.
    /// </remarks>
    private async Task<List<TextChunk>> SampleChunksAsync(
        int sampleCount, int? seed, CancellationToken cancellationToken)
    {
        // Unseeded stays the old non-deterministic behaviour; a seed makes the draw repeatable.
        var random = seed is { } value ? new Random(value) : new Random();
        var reservoir = new ReservoirSampler<TextChunk>(sampleCount, random);

        var documents = await _dataManager.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var document in documents)
        {
            var chunks = await _dataManager
                .GetChunksAsync(document.DocumentId.Value, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < chunks.Count; i++)
                reservoir.Offer(chunks[i]);
        }

        return reservoir.ToList();
    }

    /// <summary>Generates one sample from one chunk, or the reason there is no sample.</summary>
    /// <remarks>
    /// <para>
    /// A sample with an empty question is not a sample. Emitting one used to look like graceful
    /// handling and was the opposite: it entered the dataset as valid and every evaluator
    /// downstream then scored it — Answer Relevance embeds <c>""</c> and returns a cosine
    /// similarity like any other — so the corruption was invisible from that point on.
    /// </para>
    /// <para>
    /// An empty reference answer drops the sample too, because Context Precision and Context Recall
    /// both <i>throw</i> on one. Emitting it would move the failure from here, where the cause is
    /// known, to an evaluation run that cannot explain it.
    /// </para>
    /// <para>
    /// The generation is not retried. That is deliberate: it is speculative, it doubles the cost
    /// model, and nobody has asked for it.
    /// </para>
    /// </remarks>
    private async Task<GenerationResult> GenerateSampleAsync(
        TextChunk chunk,
        DatasetGenerationMode mode,
        CancellationToken ct)
    {
        var question = await GenerateQuestionAsync(chunk.Text, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(question))
            return new GenerationResult(null, EvaluationDataset.SkipReasons.EmptyQuestion);

        var referenceAnswer = string.Empty;
        if (mode == DatasetGenerationMode.QuestionAndAnswer)
        {
            referenceAnswer = await GenerateAnswerAsync(chunk.Text, question, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(referenceAnswer))
                return new GenerationResult(null, EvaluationDataset.SkipReasons.EmptyReferenceAnswer);
        }

        return new GenerationResult(
            new EvaluationSample(
                Question: question,
                PredictedAnswer: string.Empty,
                ReferenceAnswer: referenceAnswer,
                SourceChunks: [chunk.Text]),
            SkipReason: null);
    }

    private async Task<string> GenerateQuestionAsync(string chunkText, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Generate a single question whose answer is found in the provided text. " +
                "Output only the question, no explanation."),
            new(ChatRole.User, chunkText),
        };
        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }

    private async Task<string> GenerateAnswerAsync(string chunkText, string question, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "Answer the question using only the provided text. " +
                "Output only the answer, no explanation."),
            new(ChatRole.User, $"Text: {chunkText}\n\nQuestion: {question}"),
        };
        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }

    /// <summary>One chunk's outcome: a sample, or the reason it did not become one.</summary>
    /// <remarks>
    /// Exactly one side is populated. A struct rather than a nullable sample alone because the
    /// reason has to survive the fan-out to be counted — dropping it here would leave the caller
    /// with a shorter list and no account of it, which is the defect this replaces.
    /// </remarks>
    /// <param name="Sample">The generated sample, or null when generation produced nothing usable.</param>
    /// <param name="SkipReason">An <see cref="EvaluationDataset.SkipReasons"/> value, or null on success.</param>
    private readonly record struct GenerationResult(EvaluationSample? Sample, string? SkipReason);
}
