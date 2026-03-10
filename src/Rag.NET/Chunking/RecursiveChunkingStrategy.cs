using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class RecursiveChunkingStrategy : IChunkingStrategy
{
    private static readonly string[] Separators = ["\n\n", "\n", ". ", " "];

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        int chunkIndex = 0;
        foreach (var text in SplitRecursively(section.Text, options.MaxChunkSize, 0))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new TextChunk
            {
                Text = text,
                DocumentId = section.DocumentId,
                ChunkIndex = chunkIndex++,
                StartPosition = 0,
                EndPosition = text.Length,
            };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static IEnumerable<string> SplitRecursively(string text, int maxSize, int separatorIndex)
    {
        if (separatorIndex >= Separators.Length)
        {
            return HardSplit(text, maxSize);
        }

        var parts = text.Split(Separators[separatorIndex]);

        if (parts.Length <= 1)
        {
            return text.Length <= maxSize
                ? YieldTrimmed(text)
                : SplitRecursively(text, maxSize, separatorIndex + 1);
        }

        return SplitParts(parts, maxSize, separatorIndex);
    }

    private static IEnumerable<string> SplitParts(string[] parts, int maxSize, int separatorIndex)
    {
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (part.Length <= maxSize)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
            else
            {
                foreach (var sub in SplitRecursively(part, maxSize, separatorIndex + 1))
                {
                    yield return sub;
                }
            }
        }
    }

    private static IEnumerable<string> HardSplit(string text, int maxSize)
    {
        if (text.Length <= maxSize)
        {
            return YieldTrimmed(text);
        }

        return HardSplitCore(text, maxSize);
    }

    private static IEnumerable<string> HardSplitCore(string text, int maxSize)
    {
        for (int i = 0; i < text.Length; i += maxSize)
        {
            var segment = text.Substring(i, Math.Min(maxSize, text.Length - i)).Trim();
            if (segment.Length > 0)
            {
                yield return segment;
            }
        }
    }

    private static IEnumerable<string> YieldTrimmed(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            yield return trimmed;
        }
    }
}
