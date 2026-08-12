using System.Runtime.CompilerServices;
using System.Text;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Inject;

namespace Rag.NET.Chunking;

[Singleton(As = typeof(IChunkingStrategy))]
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

        var sourceText = section.Text;
        int chunkIndex = 0;
        int cursor = 0;
        string? previousChunkText = null;

        foreach (var text in SplitRecursively(sourceText, options.MaxChunkSize, 0))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Find where this chunk appears in the source text, starting from the cursor
            int pos = FindChunkStart(sourceText, text, cursor);

            int startPosition = pos;
            int endPosition = pos + text.Length;

            // Apply overlap: prepend characters from the end of the previous chunk
            string chunkText;
            if (options.Overlap > 0 && previousChunkText != null)
            {
                int overlapLength = Math.Min(options.Overlap, previousChunkText.Length);

                // The overlap is counted back from the end, so it can land inside a surrogate
                // pair just as the hard split can. Backing the start up widens the overlap by one
                // code unit and keeps the character whole; trimming it forwards would drop the
                // pair's leading half and leave the same invalid string this guards against.
                int overlapStart = RuneBoundary.AtOrBefore(
                    previousChunkText, previousChunkText.Length - overlapLength);
                string overlapText = previousChunkText[overlapStart..];
                chunkText = overlapText + text;
            }
            else
            {
                chunkText = text;
            }

            yield return new TextChunk
            {
                Text = chunkText,
                DocumentId = section.DocumentId,
                ChunkIndex = chunkIndex++,
                StartPosition = startPosition,
                EndPosition = endPosition,
                Metadata = PageMetadata.ForPage(section.PageNumber),
            };

            cursor = endPosition;
            previousChunkText = text;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static int FindChunkStart(string sourceText, string text, int cursor)
    {
        int pos = sourceText.IndexOf(text, cursor, StringComparison.Ordinal);
        if (pos < 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Chunk text of {text.Length} characters was not found in the section from offset {cursor}. ") +
                "Every chunk must be an exact substring of the section text; this indicates the splitter " +
                "fabricated text rather than reproducing it.");
        }

        return pos;
    }

    private static IEnumerable<string> SplitRecursively(string text, int maxSize, int separatorIndex)
    {
        if (text.Length <= maxSize)
        {
            return YieldTrimmed(text);
        }

        if (separatorIndex >= Separators.Length)
        {
            return HardSplitCore(text, maxSize);
        }

        var parts = text.Split(Separators[separatorIndex]);

        if (parts.Length <= 1)
        {
            return SplitRecursively(text, maxSize, separatorIndex + 1);
        }

        return SplitParts(parts, maxSize, separatorIndex);
    }

    private static IEnumerable<string> SplitParts(string[] parts, int maxSize, int separatorIndex)
    {
        var separator = Separators[separatorIndex];
        var pending = new List<string>();

        foreach (var part in parts)
        {
            if (part.Length <= maxSize)
            {
                pending.Add(part);
                continue;
            }

            foreach (var packed in Pack(pending, separator, maxSize))
            {
                yield return packed;
            }

            pending.Clear();

            foreach (var sub in SplitRecursively(part, maxSize, separatorIndex + 1))
            {
                yield return sub;
            }
        }

        foreach (var packed in Pack(pending, separator, maxSize))
        {
            yield return packed;
        }
    }

    private static IEnumerable<string> Pack(List<string> parts, string separator, int maxSize)
    {
        if (parts.Count == 0)
        {
            yield break;
        }

        var buffer = new StringBuilder(parts[0]);

        for (var i = 1; i < parts.Count; i++)
        {
            if (buffer.Length + separator.Length + parts[i].Length <= maxSize)
            {
                buffer.Append(separator).Append(parts[i]);
                continue;
            }

            var flushed = buffer.ToString().Trim();
            if (flushed.Length > 0)
            {
                yield return flushed;
            }

            buffer.Clear().Append(parts[i]);
        }

        var last = buffer.ToString().Trim();
        if (last.Length > 0)
        {
            yield return last;
        }
    }

    /// <summary>
    /// The last resort: no separator divides this text, so it is cut to length.
    /// </summary>
    /// <remarks>
    /// The cut respects character boundaries. A fixed <c>i += maxSize</c> stride does not, and
    /// bisecting an emoji leaves two lone surrogates that no embedder can normalize — see
    /// <see cref="RuneBoundary"/>.
    /// </remarks>
    private static IEnumerable<string> HardSplitCore(string text, int maxSize)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = RuneBoundary.AtOrBefore(text, Math.Min(start + maxSize, text.Length));

            // Only reachable when maxSize is narrower than the character sitting at `start`,
            // which backing up cannot solve. Emitting that character whole overruns the budget
            // by a single code unit; the alternatives are to split it — the defect this method
            // exists to avoid — or to drop it, and silently altering the caller's text is never
            // the right answer.
            if (end <= start)
            {
                end = Math.Min(start + 2, text.Length);
            }

            var segment = text[start..end].Trim();
            if (segment.Length > 0)
            {
                yield return segment;
            }

            start = end;
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
