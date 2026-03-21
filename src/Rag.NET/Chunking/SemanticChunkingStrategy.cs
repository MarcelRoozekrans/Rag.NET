using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed partial class SemanticChunkingStrategy(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SemanticChunkingOptions options) : IChunkingStrategy
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder = embedder;
    private readonly SemanticChunkingOptions _options = options;

    // Sentence-ending punctuation followed by whitespace, with negative lookbehind
    // for common abbreviations (Mr., Mrs., Ms., Dr., Jr., Sr., vs., etc., e.g., i.e.)
#pragma warning disable MA0009 // Lookbehind required for abbreviation handling
    [GeneratedRegex(@"(?<!\b(?:Mr|Mrs|Ms|Dr|Jr|Sr|vs|etc|e\.g|i\.e))\.\s+|[!?]\s+", RegexOptions.ExplicitCapture)]
    private static partial Regex SentenceEndPattern();
#pragma warning restore MA0009

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var parts = SentenceEndPattern().Split(text);
        var sentences = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                sentences.Add(trimmed);
        }
        return sentences;
    }

    internal static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sentences = SplitSentences(section.Text);
        if (sentences.Count == 0)
            yield break;

        if (sentences.Count == 1)
        {
            yield return new TextChunk
            {
                Text = sentences[0],
                DocumentId = section.DocumentId,
                ChunkIndex = 0,
                StartPosition = 0,
                EndPosition = sentences[0].Length,
            };
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var activeEmbedder = _options.ChunkingEmbedder ?? _embedder;
        var embeddings = await activeEmbedder.GenerateAsync(sentences, cancellationToken: cancellationToken).ConfigureAwait(false);

        var similarities = ComputeConsecutiveSimilarities(sentences, embeddings);
        var groups = GroupSentencesByBreakpoints(sentences, similarities);

        int chunkIndex = 0;
        int cursor = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = BuildChunk(section, groups[g], chunkIndex, cursor);
            if (chunk is null)
                continue;
            chunkIndex++;
            cursor = chunk.EndPosition;
            yield return chunk;
        }
    }

    private static double[] ComputeConsecutiveSimilarities(
        IReadOnlyList<string> sentences,
        GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        var similarities = new double[sentences.Count - 1];
        for (int i = 0; i < similarities.Length; i++)
            similarities[i] = CosineSimilarity(embeddings[i].Vector.Span, embeddings[i + 1].Vector.Span);
        return similarities;
    }

    private List<List<string>> GroupSentencesByBreakpoints(
        IReadOnlyList<string> sentences,
        double[] similarities)
    {
        var percentile = Math.Clamp(_options.BreakpointPercentile, 0.01f, 0.99f);
        var sorted = similarities.OrderBy(s => s).ToArray();
        var thresholdIndex = (int)Math.Floor(percentile * sorted.Length);
        var threshold = sorted[Math.Min(thresholdIndex, sorted.Length - 1)];

        var groups = new List<List<string>> { new() { sentences[0] } };
        for (int i = 0; i < similarities.Length; i++)
        {
            if (similarities[i] < threshold)
                groups.Add(new List<string>());
            groups[^1].Add(sentences[i + 1]);
        }
        return groups;
    }

    private static TextChunk? BuildChunk(DocumentSection section, List<string> group, int chunkIndex, int cursor)
    {
        var chunkText = string.Join(" ", group);
        if (string.IsNullOrWhiteSpace(chunkText))
            return null;

        var startPos = section.Text.IndexOf(group[0], cursor, StringComparison.Ordinal);
        if (startPos < 0) startPos = cursor;
        var lastSentence = group[^1];
        var lastPos = section.Text.IndexOf(lastSentence, startPos, StringComparison.Ordinal);
        var endPos = lastPos >= 0 ? lastPos + lastSentence.Length : startPos + chunkText.Length;

        return new TextChunk
        {
            Text = chunkText,
            DocumentId = section.DocumentId,
            ChunkIndex = chunkIndex,
            StartPosition = startPos,
            EndPosition = endPos,
        };
    }
}
