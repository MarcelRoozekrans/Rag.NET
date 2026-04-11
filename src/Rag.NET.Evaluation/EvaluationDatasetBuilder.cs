using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Evaluation;

/// <summary>
/// Generates synthetic evaluation samples from an existing document corpus.
/// Samples random chunks from <see cref="IRagDataManager"/> and uses an LLM
/// to generate a question (and optionally a reference answer) per chunk.
/// </summary>
public sealed class EvaluationDatasetBuilder(
    IRagDataManager dataManager,
    IChatClient chatClient)
{
    public async Task<IReadOnlyList<EvaluationSample>> BuildAsync(
        EvaluationDatasetBuilderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EvaluationDatasetBuilderOptions();

        // Collect all chunks across all documents
        var documents = await dataManager.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var allChunks = new List<TextChunk>();
        foreach (var doc in documents)
        {
            var chunks = await dataManager.GetChunksAsync(doc.DocumentId.Value, cancellationToken).ConfigureAwait(false);
            allChunks.AddRange(chunks);
        }

        // Random sample without replacement, clamped to available count
        var sampleCount = Math.Min(options.SampleCount, allChunks.Count);
        if (sampleCount <= 0)
            return [];

        var sampled = allChunks.OrderBy(_ => Random.Shared.Next()).Take(sampleCount).ToList();

        // Generate samples concurrently
        var tasks = sampled.Select(chunk => GenerateSampleAsync(chunk, options.Mode, cancellationToken));
        var samples = await Task.WhenAll(tasks).ConfigureAwait(false);
        return samples;
    }

    private async Task<EvaluationSample> GenerateSampleAsync(
        TextChunk chunk,
        DatasetGenerationMode mode,
        CancellationToken ct)
    {
        var question = await GenerateQuestionAsync(chunk.Text, ct).ConfigureAwait(false);

        var referenceAnswer = string.Empty;
        if (mode == DatasetGenerationMode.QuestionAndAnswer)
            referenceAnswer = await GenerateAnswerAsync(chunk.Text, question, ct).ConfigureAwait(false);

        return new EvaluationSample(
            Question: question,
            PredictedAnswer: string.Empty,
            ReferenceAnswer: referenceAnswer,
            SourceChunks: [chunk.Text]);
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
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
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
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        return response.Messages.LastOrDefault()?.Text?.Trim() ?? string.Empty;
    }
}
