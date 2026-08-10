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

            int end = Math.Min(position + options.MaxChunkSize, text.Length);

            // Try to break at a space boundary if not at the end
            if (end < text.Length)
            {
                int lastSpace = text.LastIndexOf(' ', end - 1, end - position);
                if (lastSpace > position)
                {
                    end = lastSpace;
                }
            }

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

            position += advance;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
