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
    private readonly int? _windowSizeTokens;
    private readonly int? _overlapTokens;

    /// <summary>Gets the model name used to create the tokenizer encoding.</summary>
    public string ModelName { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="TokenAwareChunkingStrategy"/>.
    /// </summary>
    /// <param name="modelName">
    /// The model name used to select the tokenizer encoding (e.g., "gpt-4", "gpt-3.5-turbo").
    /// Defaults to "gpt-4" which uses the cl100k_base encoding.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="modelName"/> is null or whitespace.</exception>
    public TokenAwareChunkingStrategy(string modelName = "gpt-4")
        : this(CreateOptions(modelName))
    {
    }

    private static TokenAwareChunkingOptions CreateOptions(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return new TokenAwareChunkingOptions { ModelName = modelName };
    }

    /// <summary>
    /// Initializes a new instance of <see cref="TokenAwareChunkingStrategy"/> from
    /// <see cref="TokenAwareChunkingOptions"/>. When <see cref="TokenAwareChunkingOptions.WindowSizeTokens"/>
    /// or <see cref="TokenAwareChunkingOptions.OverlapTokens"/> are set they override the
    /// <see cref="ChunkingOptions"/> values passed to <see cref="ChunkAsync"/>.
    /// </summary>
    /// <param name="options">The token-aware chunking options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <see cref="TokenAwareChunkingOptions.ModelName"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="TokenAwareChunkingOptions.WindowSizeTokens"/> is not positive,
    /// <see cref="TokenAwareChunkingOptions.OverlapTokens"/> is negative,
    /// or the overlap is greater than or equal to the window size.
    /// </exception>
    public TokenAwareChunkingStrategy(TokenAwareChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
#pragma warning disable MA0015 // Intentional composite param name: the invalid value lives on options.ModelName, not options itself.
        ArgumentException.ThrowIfNullOrWhiteSpace(
            options.ModelName, $"{nameof(options)}.{nameof(TokenAwareChunkingOptions.ModelName)}");
#pragma warning restore MA0015

        if (options.WindowSizeTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"WindowSizeTokens ({options.WindowSizeTokens}) must be greater than zero.");
        }

        if (options.OverlapTokens is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"OverlapTokens ({options.OverlapTokens}) must not be negative.");
        }

        if (options is { WindowSizeTokens: int window, OverlapTokens: int overlap } && overlap >= window)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"OverlapTokens ({overlap}) must be less than WindowSizeTokens ({window}).");
        }

        ModelName = options.ModelName;
        _tokenizer = TiktokenTokenizer.CreateForModel(options.ModelName);

        // Snapshot — post-construction mutation of the caller's options instance must not affect this strategy.
        _windowSizeTokens = options.WindowSizeTokens;
        _overlapTokens = options.OverlapTokens;
    }

    public async IAsyncEnumerable<TextChunk> ChunkAsync(
        DocumentSection section,
        ChunkingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (window, overlap) = ResolveWindowAndOverlap(options);

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
        var sliceBuffer = new List<int>(window);

        int chunkIndex = 0;
        int position = 0;
        int step = Math.Max(1, window - overlap);

        while (position < tokenIds.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(position + window, tokenIds.Length);

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
                    Metadata = PageMetadata.ForPage(section.PageNumber),
                };
            }

            position += step;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private (int Window, int Overlap) ResolveWindowAndOverlap(ChunkingOptions options)
    {
        var window = _windowSizeTokens ?? options.MaxChunkSize;
        var overlap = _overlapTokens ?? options.Overlap;

        if (overlap >= window)
        {
            var windowSource = _windowSizeTokens.HasValue
                ? $"{nameof(TokenAwareChunkingOptions)}.{nameof(TokenAwareChunkingOptions.WindowSizeTokens)}"
                : $"{nameof(ChunkingOptions)}.{nameof(ChunkingOptions.MaxChunkSize)}";
            var overlapSource = _overlapTokens.HasValue
                ? $"{nameof(TokenAwareChunkingOptions)}.{nameof(TokenAwareChunkingOptions.OverlapTokens)}"
                : $"{nameof(ChunkingOptions)}.{nameof(ChunkingOptions.Overlap)}";

            throw new ArgumentOutOfRangeException(nameof(options),
                $"Effective overlap {overlap} (from {overlapSource}) must be less than effective window {window} (from {windowSource}).");
        }

        return (window, overlap);
    }
}
