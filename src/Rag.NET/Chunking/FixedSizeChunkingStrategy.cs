using System.Runtime.CompilerServices;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class FixedSizeChunkingStrategy : IChunkingStrategy
{
    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        var text = section.Text;
        int chunkIndex = 0;
        int position = 0;

        while (position < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = FindChunkEnd(text, position, options.MaxChunkSize);

            var chunkText = text[position..end].Trim();

            if (chunkText.Length > 0)
            {
                yield return new TextChunk
                {
                    Text = chunkText,
                    DocumentId = section.DocumentId,
                    ChunkIndex = chunkIndex++,
                    StartPosition = position,
                    EndPosition = end,
                    Metadata = PageMetadata.ForPage(section.PageNumber),
                };
            }

            int advance = end - position - options.Overlap;
            if (advance <= 0)
            {
                advance = end - position;
            }

            // Overlap walks the next chunk's start backwards, which can land it inside a pair
            // even though `end` was legal. When backing up would stall the loop the offset is
            // instead stepped over the whole character, which is both legal and progress.
            var next = RuneBoundary.AtOrBefore(text, position + advance);
            position = next > position ? next : position + 2;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Gets where the chunk starting at <paramref name="position"/> ends.
    /// </summary>
    /// <param name="text">The section text.</param>
    /// <param name="position">Where the chunk starts, in UTF-16 code units.</param>
    /// <param name="maxChunkSize">The budget, in UTF-16 code units.</param>
    /// <returns>The end offset, always greater than <paramref name="position"/>.</returns>
    private static int FindChunkEnd(string text, int position, int maxChunkSize)
    {
        int end = Math.Min(position + maxChunkSize, text.Length);

        // Try to break at a space boundary if not at the end
        if (end < text.Length)
        {
            int lastSpace = text.LastIndexOf(' ', end - 1, end - position);
            if (lastSpace > position)
            {
                return lastSpace;
            }
        }

        // No space to break on, so the budget offset stands — and a raw code-unit offset can
        // bisect a surrogate pair. See RuneBoundary: either half alone is an invalid string
        // that no embedder can normalize.
        end = RuneBoundary.AtOrBefore(text, end);

        // Only when maxChunkSize is narrower than the character at `position`. Emitting it
        // whole overruns the budget by one code unit, which beats splitting or dropping it.
        return end > position ? end : Math.Min(position + 2, text.Length);
    }
}
