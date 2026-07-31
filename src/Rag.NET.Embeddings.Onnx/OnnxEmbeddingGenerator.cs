using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> backed by a local ONNX sentence-embedding
/// model with a BERT/WordPiece tokenizer: the only dense embedder in Rag.NET that runs offline,
/// free, with no external API.
/// <para>
/// Each text is wrapped in [CLS] … [SEP], TRUNCATED to
/// <see cref="OnnxEmbeddingOptions.MaxTokens"/> (a single pooled vector per text is the contract,
/// so windowing and stitching — what <see cref="OnnxTokenEmbeddingGenerator"/> does — would
/// compute a different, unvalidated quantity), then mean-pooled over the token axis and
/// L2-normalised. Normalised vectors make a dot product a cosine similarity.
/// </para>
/// <para>
/// <b>Padding is excluded from the mean.</b> Texts are batched
/// (<see cref="OnnxEmbeddingOptions.BatchSize"/>) and padded to the longest sequence in the pass,
/// so a short text's neighbours decide how many [PAD] rows the model returns for it. Those rows
/// are masked out of both the sum and the divisor, which is what makes a text's vector
/// independent of what it was batched with — including padding shifts every downstream number
/// silently. Batching is forced to one text per pass when the model does not declare
/// <c>attention_mask</c>, because the model would then attend to the padding and no pooling
/// choice could repair that.
/// </para>
/// <para>
/// [CLS] and [SEP] ARE included in the mean, matching sentence-transformers' mean pooling (its
/// mask is the tokenizer's, which marks both as real tokens). Excluding them is a defensible
/// choice that produces different numbers than every published figure for these models.
/// </para>
/// <para>
/// Model I/O: <c>input_ids</c> is always supplied; <c>attention_mask</c> and
/// <c>token_type_ids</c> only when the model declares them. The output is resolved by
/// <see cref="OnnxEmbeddingOptions.OutputName"/> (falling back to the model's single output) and
/// validated to be rank 3 <c>[batch, sequence, dimension]</c> — a pooled export is rejected
/// rather than trusted, since its pooling cannot be shown to exclude padding.
/// </para>
/// <para>
/// Thread safety: <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/> is
/// thread-safe and this class holds only readonly state after construction, so one instance may
/// be shared across threads. Inference is synchronous ONNX work and runs on a background thread
/// via <see cref="Task.Run(Action)"/>; cancellation is honored between batches.
/// </para>
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>[CLS] and [SEP] positions reserved per sequence.</summary>
    private const int SpecialTokensPerSequence = 2;

    /// <summary>
    /// One model pass over a padded batch. <paramref name="inputIds"/> and
    /// <paramref name="attentionMask"/> are row-major <c>[batchSize, sequenceLength]</c>; the
    /// result is the row-major token-level hidden states
    /// <c>[batchSize, sequenceLength, dimension]</c> plus that dimension. Internal seam so tests
    /// can exercise <see cref="GenerateAsync"/> — pooling included — without an ONNX model.
    /// </summary>
    internal delegate (float[] Hidden, int Dimension) BatchRunner(
        long[] inputIds, long[] attentionMask, int batchSize, int sequenceLength);

    private readonly InferenceSession? _session;
    private readonly BertTokenizer _tokenizer;
    private readonly OnnxEmbeddingOptions _options;
    private readonly BatchRunner _runBatch;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly string? _outputName;
    private readonly bool _sendAttentionMask;
    private readonly bool _sendTokenTypeIds;
    private readonly int _maxBatchSize;
    private readonly long _paddingTokenId;

    public OnnxEmbeddingGenerator(OnnxEmbeddingOptions options, ILogger<OnnxEmbeddingGenerator>? logger = null)
        : this(options, batchRunner: null, logger)
    {
    }

    /// <summary>
    /// Test seam: a non-null <paramref name="batchRunner"/> replaces the ONNX session (no model
    /// file needed — only the vocabulary). Public-constructor behavior is unchanged.
    /// </summary>
    internal OnnxEmbeddingGenerator(
        OnnxEmbeddingOptions options, BatchRunner? batchRunner, ILogger<OnnxEmbeddingGenerator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTokens <= SpecialTokensPerSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxTokens ({options.MaxTokens}) must exceed the {SpecialTokensPerSequence} positions reserved for [CLS]/[SEP].");
        }

        if (options.BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(options), $"BatchSize ({options.BatchSize}) must be at least 1.");

        if (batchRunner is null)
            BertOnnxPlumbing.EnsureModelFileExists(options.ModelPath);

        BertOnnxPlumbing.EnsureVocabularyFileExists(options.TokenizerVocabPath);

        _options = options;
        _tokenizer = BertOnnxPlumbing.CreateTokenizer(options.TokenizerVocabPath);
        _paddingTokenId = _tokenizer.PaddingTokenId;
        // The ingestion path stamps this on stored vectors (EmbeddingModelIdentity); a generator
        // reporting no model id disables embedding versioning instead of failing, which surfaces
        // much later as vectors that never get re-indexed.
        _metadata = new EmbeddingGeneratorMetadata("onnx", defaultModelId: ResolveModelId(options));

        if (batchRunner is not null)
        {
            _runBatch = batchRunner;
            _sendAttentionMask = true;
            _maxBatchSize = options.BatchSize;
            return;
        }

        _session = new InferenceSession(options.ModelPath);
        _outputName = BertOnnxPlumbing.ResolveOutputName([.. _session.OutputMetadata.Keys], options.OutputName,
            "Set OnnxEmbeddingOptions.OutputName to the output holding the token-level hidden states [batch, sequence, dimension].");
        _sendAttentionMask = _session.InputMetadata.ContainsKey("attention_mask");
        _sendTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        // Without attention_mask the model attends to the padding, so padding must not exist:
        // one text per pass, where the sequence length is that text's own length.
        _maxBatchSize = _sendAttentionMask ? options.BatchSize : 1;
        _runBatch = RunBatchCore;

        if (!_sendAttentionMask && options.BatchSize > 1 && logger is not null)
            OnnxEmbeddingsLog.BatchingDisabledWithoutAttentionMask(logger, options.BatchSize, options.ModelPath);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="options"/> is otherwise ignored: a local export has one fixed model and
    /// one fixed dimension. A <see cref="EmbeddingGenerationOptions.Dimensions"/> request that
    /// the model cannot satisfy fails loudly rather than returning a differently sized vector.
    /// </remarks>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var texts = values as IReadOnlyList<string> ?? [.. values];
        var sequences = new long[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            sequences[i] = Encode(texts[i]);

        var embeddings = new Embedding<float>[texts.Count];
        for (var start = 0; start < sequences.Length; start += _maxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(start + _maxBatchSize, sequences.Length);
            await EmbedBatchAsync(sequences, embeddings, start, end, options?.Dimensions, cancellationToken)
                .ConfigureAwait(false);
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
            return _metadata;

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// Tokenizes one text into <c>[CLS] content [SEP]</c> ids, truncating the content to the
    /// per-sequence budget.
    /// </summary>
    private long[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // No special tokens from the tokenizer: they are added here, so truncation cannot drop
        // the [SEP] the model expects at the end of the sequence. Offsets are discarded, but the
        // whitespace substitution still matters: without it the normalizer deletes newlines and
        // the words either side are embedded as one the document never contained.
        var tokens = BertOnnxPlumbing.EncodeToTokens(_tokenizer, text, out _, out _);
        var contentLength = Math.Min(tokens.Count, _options.MaxTokens - SpecialTokensPerSequence);

        var ids = new long[contentLength + SpecialTokensPerSequence];
        ids[0] = _tokenizer.ClassificationTokenId;
        for (var i = 0; i < contentLength; i++)
            ids[i + 1] = tokens[i].Id;
        ids[^1] = _tokenizer.SeparatorTokenId;

        return ids;
    }

    /// <summary>
    /// Runs one padded pass over <c>sequences[start..end)</c> and writes the pooled, normalised
    /// vector for each row into <paramref name="embeddings"/> at the same index.
    /// </summary>
    private async Task EmbedBatchAsync(
        long[][] sequences,
        Embedding<float>[] embeddings,
        int start,
        int end,
        int? requestedDimensions,
        CancellationToken cancellationToken)
    {
        var batchSize = end - start;
        var sequenceLength = 0;
        foreach (ref readonly var sequence in sequences.AsSpan(start, batchSize))
            sequenceLength = Math.Max(sequenceLength, sequence.Length);

        // The span never crosses the await below, so it cannot be captured by the state machine.
        var (inputIds, attentionMask) = BuildFeed(sequences.AsSpan(start, batchSize), sequenceLength);

        // InferenceSession.Run is synchronous — keep it off the caller's thread.
        var (hidden, dimension) = await Task.Run(
            () => _runBatch(inputIds, attentionMask, batchSize, sequenceLength), cancellationToken)
            .ConfigureAwait(false);

        if (dimension <= 0)
            throw new InvalidOperationException($"Model returned a non-positive embedding dimension ({dimension}).");

        var expected = (long)batchSize * sequenceLength * dimension;
        if (hidden.Length != expected)
        {
            throw new InvalidOperationException(
                $"Model returned {hidden.Length} hidden values for a [{batchSize}, {sequenceLength}, {dimension}] " +
                $"batch, which needs {expected}.");
        }

        if (requestedDimensions is int requested && requested != dimension)
        {
            throw new NotSupportedException(
                $"EmbeddingGenerationOptions.Dimensions requested {requested} but this ONNX model produces " +
                $"{dimension}-dimensional embeddings; a local export has one fixed dimension.");
        }

        for (var row = 0; row < batchSize; row++)
        {
            var vector = MeanPool(
                hidden.AsSpan(row * sequenceLength * dimension, sequenceLength * dimension),
                attentionMask.AsSpan(row * sequenceLength, sequenceLength),
                dimension);
            L2NormalizeInPlace(vector);
            embeddings[start + row] = new Embedding<float>(vector) { ModelId = _metadata.DefaultModelId };
        }
    }

    /// <summary>
    /// Builds the row-major <c>[batchSize, sequenceLength]</c> input ids and attention mask:
    /// shorter sequences are right-padded with the vocabulary's [PAD] id and their mask entries
    /// stay 0.
    /// </summary>
    private (long[] InputIds, long[] AttentionMask) BuildFeed(
        ReadOnlySpan<long[]> batch, int sequenceLength)
    {
        var inputIds = new long[batch.Length * sequenceLength];
        var attentionMask = new long[batch.Length * sequenceLength];

        for (var row = 0; row < batch.Length; row++)
        {
            var sequence = batch[row];
            var rowOffset = row * sequenceLength;
            for (var t = 0; t < sequenceLength; t++)
            {
                if (t < sequence.Length)
                {
                    inputIds[rowOffset + t] = sequence[t];
                    attentionMask[rowOffset + t] = 1;
                }
                else
                {
                    inputIds[rowOffset + t] = _paddingTokenId;
                }
            }
        }

        return (inputIds, attentionMask);
    }

    /// <summary>
    /// Mean-pools one sequence's token rows, counting ONLY the tokens
    /// <paramref name="attentionMask"/> marks as real: masked rows contribute to neither the sum
    /// nor the divisor. That is what makes a text's vector independent of the batch it was padded
    /// into.
    /// </summary>
    /// <param name="hidden">Row-major <c>[tokens, dimension]</c> hidden states for one sequence.</param>
    /// <param name="attentionMask">One entry per token row; 0 marks padding.</param>
    /// <param name="dimension">The model's embedding dimension.</param>
    /// <returns>The pooled vector, not yet normalised.</returns>
    internal static float[] MeanPool(ReadOnlySpan<float> hidden, ReadOnlySpan<long> attentionMask, int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        if (hidden.Length != attentionMask.Length * dimension)
        {
            throw new ArgumentException(
                $"Hidden states length ({hidden.Length}) does not match {attentionMask.Length} token rows " +
                $"of dimension {dimension}.",
                nameof(hidden));
        }

        var pooled = new float[dimension];
        var included = 0;
        for (var token = 0; token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
                continue; // padding: contributes nothing, and does not grow the divisor

            included++;
            var row = hidden.Slice(token * dimension, dimension);
            for (var d = 0; d < dimension; d++)
                pooled[d] += row[d];
        }

        if (included == 0)
        {
            throw new ArgumentException(
                "The attention mask marks every token row as padding; mean pooling has nothing to average.",
                nameof(attentionMask));
        }

        foreach (ref var value in pooled.AsSpan())
            value /= included;

        return pooled;
    }

    /// <summary>
    /// Scales <paramref name="vector"/> to unit length in place. A zero vector is left as it is —
    /// there is no direction to preserve, and dividing by its norm would produce NaNs that only
    /// surface much later as unexplainable similarity scores.
    /// </summary>
    /// <returns><see langword="true"/> when the vector was scaled; <see langword="false"/> for a zero vector.</returns>
    internal static bool L2NormalizeInPlace(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        double normSquared = 0;
        foreach (ref readonly var value in vector.AsSpan())
            normSquared += (double)value * value;

        if (normSquared == 0)
            return false;

        var norm = (float)Math.Sqrt(normSquared);
        foreach (ref var value in vector.AsSpan())
            value /= norm;

        return true;
    }

    /// <summary>
    /// Resolves the model identity reported through
    /// <see cref="EmbeddingGeneratorMetadata.DefaultModelId"/>: the explicit
    /// <see cref="OnnxEmbeddingOptions.ModelId"/>, else the model file name without its
    /// extension, else <see langword="null"/> — which disables embedding versioning rather than
    /// stamping every model with the same made-up identity.
    /// </summary>
    internal static string? ResolveModelId(OnnxEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ModelId))
            return options.ModelId;

        var fileName = Path.GetFileNameWithoutExtension(options.ModelPath);
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    /// <summary>
    /// The resolved output must be the token-level hidden states — rank 3 with the pass's exact
    /// batch size and sequence length. A rank-2 <c>[batch, dimension]</c> shape indicates a
    /// pooled export, whose pooling cannot be shown to exclude padding.
    /// </summary>
    internal static void ValidateOutputShape(
        ReadOnlySpan<int> dimensions, int expectedBatchSize, int expectedSequenceLength, string outputName)
    {
        if (BertOnnxPlumbing.IsRank3WithSequenceLength(dimensions, expectedSequenceLength) &&
            dimensions[0] == expectedBatchSize)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Model output '{outputName}' has shape [{BertOnnxPlumbing.FormatShape(dimensions)}] but token-level " +
            $"hidden states [{expectedBatchSize}, {expectedSequenceLength}, dimension] are required. A " +
            "[batch, dimension] shape indicates a pooled-embedding export, whose pooling cannot be shown to " +
            "exclude padding; set OnnxEmbeddingOptions.OutputName to the token-level output or use a model " +
            "exported with token-level hidden states.");
    }

    /// <summary>Runs one padded batch through the session and copies out its hidden states.</summary>
    private (float[] Hidden, int Dimension) RunBatchCore(
        long[] inputIds, long[] attentionMask, int batchSize, int sequenceLength)
    {
        // Only feed the inputs this model actually declares (some exports omit token_type_ids or
        // attention_mask).
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [batchSize, sequenceLength])),
        };
        if (_sendAttentionMask)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "attention_mask", new DenseTensor<long>(attentionMask, [batchSize, sequenceLength])));
        }
        if (_sendTokenTypeIds)
        {
            // All zeros: single-segment input.
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                "token_type_ids", new DenseTensor<long>([batchSize, sequenceLength])));
        }

        using var outputs = _session!.Run(inputs, [_outputName!]);
        var tensor = outputs.First().AsTensor<float>();
        ValidateOutputShape(tensor.Dimensions, batchSize, sequenceLength, _outputName!);
        var dimension = tensor.Dimensions[2];

        var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
        var hidden = new float[batchSize * sequenceLength * dimension];
        dense.Buffer.Span[..hidden.Length].CopyTo(hidden);

        return (hidden, dimension);
    }

    /// <inheritdoc />
    public void Dispose() => _session?.Dispose();
}
