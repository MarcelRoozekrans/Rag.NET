using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed partial class SemanticChunkingStrategy(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    SemanticChunkingOptions options) : IChunkingStrategy, IDocumentChunkingStrategy, IChunkRefinementStrategy
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

    // chunkingOptions (MaxChunkSize/Overlap) is not used — semantic chunking uses its own
    // SemanticChunkingOptions for size constraints. The parameter is required by IChunkingStrategy.
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
                Metadata = PageMetadata.ForPage(section.PageNumber),
            };
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var activeEmbedder = _options.ChunkingEmbedder ?? _embedder;
        var embeddings = await activeEmbedder.GenerateAsync(sentences, cancellationToken: cancellationToken).ConfigureAwait(false);

        var similarities = ComputeConsecutiveSimilarities(sentences, embeddings);
        var groups = GroupSentencesByBreakpoints(sentences, similarities);

        MergeUndersizedGroups(groups, static s => s.Length);
        SplitOversizedGroups(groups, static s => s.Length);

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

    /// <remarks>
    /// All sections are expected to belong to the same document. This method stamps every
    /// chunk with the first section's DocumentId.
    /// </remarks>
    public async IAsyncEnumerable<TextChunk> ChunkDocumentAsync(
        IAsyncEnumerable<DocumentSection> sections,
        ChunkingOptions chunkingOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sectionList = new List<DocumentSection>();
        await foreach (var section in sections.WithCancellation(cancellationToken).ConfigureAwait(false))
            sectionList.Add(section);

        if (sectionList.Count == 0)
            yield break;

        Debug.Assert(
            sectionList.All(s => s.DocumentId == sectionList[0].DocumentId),
            "ChunkDocumentAsync expects all sections to belong to the same document.");

        cancellationToken.ThrowIfCancellationRequested();

        var activeEmbedder = _options.ChunkingEmbedder ?? _embedder;
        var texts = sectionList.ConvertAll(s => s.Text);
        var embeddings = await activeEmbedder.GenerateAsync(texts, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var groups = GroupSectionsByBreakpoints(sectionList, embeddings);

        // Merge/split groups based on total text length — the same helpers as the sentence path,
        // applied to the sections themselves so every group keeps the sections it was built from
        // (and with them the source-page range each chunk reports).
        MergeUndersizedGroups(groups, static s => s.Text.Length);
        SplitOversizedGroups(groups, static s => s.Text.Length);

        var documentId = sectionList[0].DocumentId;
        int chunkIndex = 0;
#pragma warning disable HLQ012 // Plain foreach is clearer here; no perf-critical path
        foreach (var group in groups)
        {
#pragma warning restore HLQ012
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Join("\n\n", group.ConvertAll(static s => s.Text));
            // GroupCharLength uses " " as separator (1 char) but we join with "\n\n" (2 chars).
            // This means MaxChunkSize is slightly under-enforced (by at most count-1 chars per group).
            // Acceptable: chunking constraints are heuristics, not hard limits.
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var pages = PageRange.Empty;
            foreach (ref readonly var section in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(group))
                pages = pages.Fold(section.PageNumber);

            var metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
            pages.WriteTo(metadata);

            yield return new TextChunk
            {
                Text = text,
                DocumentId = documentId,
                ChunkIndex = chunkIndex++,
                StartPosition = 0,
                EndPosition = text.Length,
                Metadata = metadata,
            };
        }
    }

    public async IAsyncEnumerable<TextChunk> RefineAsync(
        IAsyncEnumerable<TextChunk> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int chunkIndex = 0;
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Text.Length < _options.MinChunkSize)
            {
                yield return chunk with { ChunkIndex = chunkIndex++ };
                continue;
            }

            // Treat the oversized chunk text as a single-section document and re-split at
            // sentence boundaries using the existing per-section path. The synthetic section
            // has no page number, so each sub-chunk inherits the parent's page range — the
            // narrowest span refinement can still vouch for.
            var syntheticSection = new DocumentSection { DocumentId = chunk.DocumentId, Text = chunk.Text };

            await foreach (var sub in ChunkAsync(syntheticSection, new ChunkingOptions(), cancellationToken)
                               .ConfigureAwait(false))
            {
                if (chunk.Metadata.TryGetValue(ReservedMetadataKeys.Page, out var page))
                    sub.Metadata[ReservedMetadataKeys.Page] = page;
                if (chunk.Metadata.TryGetValue(ReservedMetadataKeys.PageEnd, out var pageEnd))
                    sub.Metadata[ReservedMetadataKeys.PageEnd] = pageEnd;
                yield return sub with { ChunkIndex = chunkIndex++ };
            }
        }
    }

    private List<List<DocumentSection>> GroupSectionsByBreakpoints(
        List<DocumentSection> sectionList,
        GeneratedEmbeddings<Embedding<float>> embeddings)
    {
        // Consecutive cosine similarities between adjacent section embeddings
        var similarities = new double[sectionList.Count - 1];
        for (int i = 0; i < similarities.Length; i++)
            similarities[i] = CosineSimilarity(embeddings[i].Vector.Span, embeddings[i + 1].Vector.Span);

        // Group adjacent sections using the same percentile breakpoint logic as sentence path
        var percentile = Math.Clamp(_options.BreakpointPercentile, 0.01f, 0.99f);
        var sorted = similarities.OrderBy(s => s).ToArray();
        var thresholdIndex = (int)Math.Floor(percentile * sorted.Length);
        var threshold = sorted.Length > 0 ? sorted[Math.Min(thresholdIndex, sorted.Length - 1)] : 0;

        var groups = new List<List<DocumentSection>> { new() { sectionList[0] } };
        for (int i = 0; i < similarities.Length; i++)
        {
            if (similarities[i] < threshold)
                groups.Add(new List<DocumentSection>());
            groups[^1].Add(sectionList[i + 1]);
        }
        return groups;
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

    private void MergeUndersizedGroups<T>(List<List<T>> groups, Func<T, int> lengthOf)
    {
        bool merged = true;
        while (merged && groups.Count > 1)
        {
            merged = false;
            for (int i = 0; i < groups.Count; i++)
            {
                var groupLength = GroupCharLength(groups[i], lengthOf);
                if (groupLength >= _options.MinChunkSize)
                    continue;

                // Pick the smaller neighbor to merge with
                int neighborIndex;
                if (i == 0)
                    neighborIndex = 1;
                else if (i == groups.Count - 1)
                    neighborIndex = i - 1;
                else
                    neighborIndex = GroupCharLength(groups[i - 1], lengthOf) <= GroupCharLength(groups[i + 1], lengthOf) ? i - 1 : i + 1;

                // Merge into the earlier index
                int target = Math.Min(i, neighborIndex);
                int source = Math.Max(i, neighborIndex);
                groups[target].AddRange(groups[source]);
                groups.RemoveAt(source);

                merged = true;
                break; // restart scan after mutation
            }
        }
    }

    private void SplitOversizedGroups<T>(List<List<T>> groups, Func<T, int> lengthOf)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            if (GroupCharLength(groups[i], lengthOf) <= _options.MaxChunkSize)
                continue;

            var subGroups = new List<List<T>>();
            var current = new List<T>();
            int currentLen = 0;

#pragma warning disable HLQ012 // Plain foreach is clearer here; no perf-critical path
            foreach (var sentence in groups[i])
#pragma warning restore HLQ012
            {
                var sentenceLength = lengthOf(sentence);
                var addedLen = currentLen == 0 ? sentenceLength : sentenceLength + 1; // +1 for space join
                if (current.Count > 0 && currentLen + addedLen > _options.MaxChunkSize)
                {
                    subGroups.Add(current);
                    current = new List<T>();
                    currentLen = 0;
                }
                current.Add(sentence);
                currentLen += currentLen == 0 ? sentenceLength : addedLen;
            }
            if (current.Count > 0)
                subGroups.Add(current);

            // Replace the original group with subgroups
            groups.RemoveAt(i);
            groups.InsertRange(i, subGroups);
            i += subGroups.Count - 1; // skip past inserted groups
        }
    }

    private static int GroupCharLength<T>(List<T> group, Func<T, int> lengthOf) =>
        group.Sum(lengthOf) + Math.Max(0, group.Count - 1); // spaces between sentences

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
            Metadata = PageMetadata.ForPage(section.PageNumber),
        };
    }
}
