using System.Runtime.CompilerServices;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

/// <summary>
/// Splits document sections into chunks based on token count rather than character count,
/// preventing chunks from exceeding embedding model token limits.
/// </summary>
public sealed class TokenAwareChunkingStrategy : IChunkingStrategy
{
    private readonly Tokenizer _tokenizer;

    /// <summary>
    /// Initializes a new instance of <see cref="TokenAwareChunkingStrategy"/>.
    /// </summary>
    /// <param name="modelName">
    /// The model name used to select the tokenizer encoding (e.g., "gpt-4", "gpt-3.5-turbo").
    /// Defaults to "gpt-4" which uses the cl100k_base encoding.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modelName"/> is null or whitespace.</exception>
    /// <summary>Gets the model name used to create the tokenizer encoding.</summary>
    public string ModelName { get; }

    public TokenAwareChunkingStrategy(string modelName = "gpt-4")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ModelName = modelName;
        _tokenizer = TiktokenTokenizer.CreateForModel(modelName);
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options.Overlap >= options.MaxChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Overlap ({options.Overlap}) must be less than MaxChunkSize ({options.MaxChunkSize}).");
        }

        if (string.IsNullOrEmpty(section.Text))
        {
            yield break;
        }

        var encodedIds = _tokenizer.EncodeToIds(section.Text);

        // Copy IReadOnlyList<int> to array once, outside the loop
        var tokenIds = new int[encodedIds.Count];
        for (int i = 0; i < encodedIds.Count; i++)
        {
            tokenIds[i] = encodedIds[i];
        }

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
