using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// <see cref="ITokenEmbeddingGenerator"/> backed by a local ONNX embedding model with a
/// BERT/WordPiece tokenizer. Accepts text of ANY length: inputs beyond
/// <see cref="OnnxTokenEmbeddingOptions.MaxTokens"/> are cut into overlapping token windows
/// (<see cref="TokenWindowStitcher.Windows"/>), each window runs through the model, and the
/// per-window matrices are stitched back into one matrix covering every token.
/// <para>
/// Special tokens: the text is tokenized WITHOUT special tokens, so
/// <see cref="TokenEmbeddingResult.TokenOffsets"/> maps 1:1 to content tokens. Each model pass
/// wraps its window in [CLS] … [SEP] (as BERT-style models expect) and the two special-token
/// rows are dropped from the output before stitching — offsets and matrix rows stay aligned.
/// Offsets come from the tokenizer's normalized view of the input; standard BERT
/// normalization (lowercasing, whitespace cleanup) is length-preserving for typical text.
/// </para>
/// <para>
/// Thread safety: <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is
/// thread-safe and this class holds only readonly state after construction, so one instance
/// may be shared across threads. Inference is synchronous ONNX work and runs on a background
/// thread via <see cref="Task.Run(Action)"/>; cancellation is honored between windows.
/// </para>
/// </summary>
public sealed class OnnxTokenEmbeddingGenerator : ITokenEmbeddingGenerator, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly OnnxTokenEmbeddingOptions _options;

    public OnnxTokenEmbeddingGenerator(OnnxTokenEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WindowOverlapTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"WindowOverlapTokens ({options.WindowOverlapTokens}) must not be negative.");
        }

        if (options.MaxTokens <= options.WindowOverlapTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxTokens ({options.MaxTokens}) must be greater than WindowOverlapTokens ({options.WindowOverlapTokens}).");
        }

        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model file not found: {options.ModelPath}", options.ModelPath);

        if (!File.Exists(options.TokenizerVocabPath))
        {
            throw new FileNotFoundException(
                $"BERT vocabulary file not found: {options.TokenizerVocabPath}", options.TokenizerVocabPath);
        }

        _options = options;
        _tokenizer = BertTokenizer.Create(options.TokenizerVocabPath);
        _session = new InferenceSession(options.ModelPath);
    }

    /// <inheritdoc />
    public int MaxTokens => _options.MaxTokens;

    /// <inheritdoc />
    public async ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        // No special tokens here: one EncodedToken per content token, offsets into the text.
        var tokens = _tokenizer.EncodeToTokens(text, out _);
        var offsets = new (int Start, int End)[tokens.Count];
        var ids = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            offsets[i] = (tokens[i].Offset.Start.Value, tokens[i].Offset.End.Value);
            ids[i] = tokens[i].Id;
        }

        if (tokens.Count == 0)
        {
            // Nothing to embed (e.g. whitespace-only input): an empty, contract-consistent
            // result (0 rows x Dimension 1 == 0 floats).
            return new TokenEmbeddingResult
            {
                Embeddings = ReadOnlyMemory<float>.Empty,
                Dimension = 1,
                TokenOffsets = [],
            };
        }

        var windows = TokenWindowStitcher.Windows(tokens.Count, _options.MaxTokens, _options.WindowOverlapTokens);
        var matrices = new List<float[]>(windows.Count);
        var dimension = 0;
        for (var w = 0; w < windows.Count; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = windows[w];
            // InferenceSession.Run is synchronous — keep it off the caller's thread.
            var (matrix, dim) = await Task.Run(
                () => RunWindow(ids, window.Start, window.End), cancellationToken).ConfigureAwait(false);
            matrices.Add(matrix);
            dimension = dim;
        }

        return new TokenEmbeddingResult
        {
            Embeddings = TokenWindowStitcher.Stitch(matrices, windows, dimension, tokens.Count),
            Dimension = dimension,
            TokenOffsets = offsets,
        };
    }

    /// <summary>
    /// Runs one [CLS] window [SEP] pass and returns the content-token rows (the two
    /// special-token rows are dropped) as a row-major matrix plus the model dimension.
    /// </summary>
    private (float[] Matrix, int Dimension) RunWindow(int[] ids, int start, int end)
    {
        var contentLength = end - start;
        var totalLength = contentLength + 2; // [CLS] + content + [SEP]

        var inputIds = new DenseTensor<long>([1, totalLength]);
        var attentionMask = new DenseTensor<long>([1, totalLength]);
        var tokenTypeIds = new DenseTensor<long>([1, totalLength]);

        inputIds[0, 0] = _tokenizer.ClassificationTokenId;
        for (var i = 0; i < contentLength; i++)
            inputIds[0, i + 1] = ids[start + i];
        inputIds[0, totalLength - 1] = _tokenizer.SeparatorTokenId;

        for (var i = 0; i < totalLength; i++)
        {
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
        };

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First().AsTensor<float>(); // last_hidden_state [1, totalLength, dim]
        var dimension = hidden.Dimensions[2];

        var matrix = new float[contentLength * dimension];
        for (var token = 0; token < contentLength; token++)
        {
            for (var d = 0; d < dimension; d++)
                matrix[(token * dimension) + d] = hidden[0, token + 1, d]; // +1 skips [CLS]
        }

        return (matrix, dimension);
    }

    public void Dispose() => _session.Dispose();
}
