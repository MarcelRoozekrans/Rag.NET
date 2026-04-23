using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.Tokenizers;
using Rag.NET.Models;

namespace Rag.NET.QueryTechniques.ContextualCompression;

/// <summary>
/// Extractive contextual compressor. Splits each chunk into sentences, embeds them,
/// scores them against the query embedding by cosine similarity, and keeps either the
/// top-N sentences or as many top-ranked sentences as fit within the token budget.
/// </summary>
public sealed partial class ExtractiveCompressor : IContextualCompressor
{
    private static readonly Regex SentenceSplit = SentenceSplitRegex();

#pragma warning disable MA0009 // Lookbehind is required to preserve sentence-ending punctuation
    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.ExplicitCapture)]
    private static partial Regex SentenceSplitRegex();
#pragma warning restore MA0009

    private static readonly Tokenizer Cl100kTokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly ContextualCompressionOptions _options;
    private readonly ILogger<ExtractiveCompressor> _logger;

    public ExtractiveCompressor(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        ContextualCompressionOptions options,
        ILogger<ExtractiveCompressor>? logger = null)
    {
        _embedder = embedder;
        _options = options;
        _logger = logger ?? NullLogger<ExtractiveCompressor>.Instance;
    }

    public async ValueTask<IReadOnlyList<SearchResult>> CompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sources.Count == 0) return sources;

        Embedding<float> queryEmbedding;
        try
        {
            var qResult = await _embedder
                .GenerateAsync(new[] { query }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            queryEmbedding = qResult[0];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogQueryEmbeddingFailed(_logger, ex);
            return sources;
        }

        var tasks = new Task<SearchResult>[sources.Count];
        for (var i = 0; i < sources.Count; i++)
            tasks[i] = CompressOneAsync(sources[i], queryEmbedding, cancellationToken);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<SearchResult> CompressOneAsync(
        SearchResult source,
        Embedding<float> queryEmbedding,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = source.Chunk.Text;
            if (string.IsNullOrWhiteSpace(text)) return source;

            var sentences = SplitSentences(text);
            if (sentences.Count == 0) return source;

            var sentEmbeddings = await _embedder
                .GenerateAsync(sentences, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var order = RankByScore(queryEmbedding, sentEmbeddings, sentences.Count);
            var compressed = SelectCompressed(sentences, order);
            if (compressed is null) return source;

            return string.IsNullOrWhiteSpace(compressed)
                ? source
                : source with { CompressedText = compressed };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogCompressionFailed(_logger, source.Chunk.DocumentId.Value, ex);
            return source;
        }
    }

    private static List<string> SplitSentences(string text)
    {
        var rawSentences = SentenceSplit.Split(text);
        var sentences = new List<string>(rawSentences.Length);
        foreach (var s in rawSentences)
        {
            if (!string.IsNullOrWhiteSpace(s)) sentences.Add(s);
        }
        return sentences;
    }

    private static int[] RankByScore(
        Embedding<float> queryEmbedding,
        GeneratedEmbeddings<Embedding<float>> sentEmbeddings,
        int count)
    {
        var scores = new double[count];
        for (var i = 0; i < count; i++)
            scores[i] = CosineSimilarity(queryEmbedding.Vector.Span, sentEmbeddings[i].Vector.Span);

        var order = new int[count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a])); // descending
        return order;
    }

    private string? SelectCompressed(List<string> sentences, int[] order)
    {
        if (_options.KeepTopSentences is { } topN)
        {
            var take = Math.Min(topN, sentences.Count);
            var kept = new int[take];
            for (var i = 0; i < take; i++) kept[i] = order[i];
            Array.Sort(kept);
            return Join(sentences, kept);
        }

        if (_options.MaxTokensPerChunk is { } maxTokens)
        {
            var keptList = new List<int>();
            var running = 0;
            foreach (var idx in order)
            {
                var tokens = Cl100kTokenizer.CountTokens(sentences[idx]);
                if (keptList.Count > 0 && running + tokens > maxTokens) break;
                keptList.Add(idx);
                running += tokens;
                if (running >= maxTokens) break;
            }
            keptList.Sort();
            return Join(sentences, keptList);
        }

        return null;
    }

    private static string Join(IReadOnlyList<string> sentences, IReadOnlyList<int> indices)
    {
        if (indices.Count == 0) return string.Empty;
        var parts = new string[indices.Count];
        for (var i = 0; i < indices.Count; i++) parts[i] = sentences[indices[i]];
        return string.Join(" ", parts);
    }

    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Extractive compression failed for chunk {DocumentId}, falling back to original text.")]
    private static partial void LogCompressionFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Extractive compression failed to embed query; returning all sources uncompressed.")]
    private static partial void LogQueryEmbeddingFailed(ILogger logger, Exception ex);
}
