using System.Runtime.CompilerServices;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public sealed class TokenAwareChunkingStrategy : IChunkingStrategy
{
    private readonly Tokenizer _tokenizer;

    public TokenAwareChunkingStrategy(string modelName = "gpt-4")
    {
        _tokenizer = TiktokenTokenizer.CreateForModel(modelName);
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        var encodedIds = _tokenizer.EncodeToIds(section.Text);

        // Copy IReadOnlyList<int> to array once, outside the loop
        var tokenIds = new int[encodedIds.Count];
        Array.Copy(encodedIds.ToArray(), tokenIds, encodedIds.Count);

        // Pre-allocate a reusable List<int> — reference type, no boxing when passed to Decode(IEnumerable<int>)
        var sliceBuffer = new List<int>(options.MaxChunkSize);

        int chunkIndex = 0;
        int position = 0;
        int step = Math.Max(1, options.MaxChunkSize - options.Overlap);

        while (position < tokenIds.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(position + options.MaxChunkSize, tokenIds.Length);

            sliceBuffer.Clear();
            foreach (ref readonly int id in tokenIds.AsSpan(position, end - position))
            {
                sliceBuffer.Add(id);
            }

            var chunkText = _tokenizer.Decode(sliceBuffer);

            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                yield return new TextChunk
                {
                    Text = chunkText.Trim(),
                    DocumentId = section.DocumentId,
                    ChunkIndex = chunkIndex++,
                    StartPosition = position,
                    EndPosition = end,
                };
            }

            position += step;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
