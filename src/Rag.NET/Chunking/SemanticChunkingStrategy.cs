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
        await Task.CompletedTask.ConfigureAwait(false);
        yield break; // Full implementation in Task 3.
    }
}
