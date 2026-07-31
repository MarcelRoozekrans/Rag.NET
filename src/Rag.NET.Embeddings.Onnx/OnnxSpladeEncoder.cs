using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Models;

namespace Rag.NET.Embeddings.Onnx;

/// <summary>
/// <see cref="ISparseEmbeddingGenerator"/> backed by a local SPLADE-style ONNX model (BERT
/// encoder + MLM head) with a BERT/WordPiece tokenizer. Per model pass the MLM logits
/// <c>[1, sequence, vocabulary]</c> are pooled to one weight per vocabulary term:
/// <c>weight_v = max over tokens of log(1 + max(0, logit))</c>. Inputs beyond
/// <see cref="OnnxSpladeOptions.MaxTokens"/> are cut into non-overlapping token windows
/// (<see cref="TokenWindowStitcher.Windows"/>) and the per-window term weights are merged by
/// element-wise max. The merged weights are pruned to the
/// <see cref="OnnxSpladeOptions.TopTerms"/> largest and emitted as an ascending-index
/// <see cref="SparseVector"/>.
/// <para>
/// Unlike <see cref="OnnxTokenEmbeddingGenerator"/>, NO token offsets are consumed here:
/// SPLADE only needs token IDS, never a mapping back into the input text, so the
/// normalization length guard that generator applies is unnecessary — length-changing
/// normalization (e.g. stripped control characters) cannot misalign anything.
/// </para>
/// <para>
/// Special tokens: each pass wraps its window in [CLS] … [SEP] (as BERT-style models expect)
/// and the two special-token rows are dropped before pooling — only content-token logits
/// contribute to term weights.
/// </para>
/// <para>
/// Model I/O and thread safety match <see cref="OnnxTokenEmbeddingGenerator"/>:
/// <c>input_ids</c> always supplied, <c>attention_mask</c>/<c>token_type_ids</c> only when
/// declared; output resolved by <see cref="OnnxSpladeOptions.OutputName"/> and validated to
/// be rank 3; inference runs on a background thread via <see cref="Task.Run(Action)"/> with
/// cancellation honored between windows; the instance holds only readonly state.
/// </para>
/// </summary>
public sealed class OnnxSpladeEncoder : ISparseEmbeddingGenerator, IDisposable
{
    /// <summary>[CLS] and [SEP] positions reserved per model pass.</summary>
    private const int SpecialTokensPerWindow = 2;

    /// <summary>
    /// One model pass over <c>ids[start..end)</c> returning the POOLED per-vocabulary term
    /// weights (<c>float[vocabularySize]</c> — <see cref="PoolWindow"/> already applied) and
    /// the vocabulary size. Pooling happens inside the pass, over the live output tensor
    /// buffer, so the full <c>[tokens, vocabulary]</c> logit matrix (tens of MB per window)
    /// is never copied out. Internal seam so tests can exercise <see cref="GenerateAsync"/>
    /// without an ONNX model.
    /// </summary>
    internal delegate (float[] Weights, int VocabularySize) WindowRunner(int[] ids, int start, int end);

    private readonly InferenceSession? _session;
    private readonly BertTokenizer _tokenizer;
    private readonly OnnxSpladeOptions _options;
    private readonly WindowRunner _runWindow;
    private readonly string? _outputName;
    private readonly bool _sendAttentionMask;
    private readonly bool _sendTokenTypeIds;
    private readonly int _tokenizerVocabularySize;
    private readonly ILogger<OnnxSpladeEncoder>? _logger;
    private int _vocabularyMismatchWarned;

    public OnnxSpladeEncoder(OnnxSpladeOptions options, ILogger<OnnxSpladeEncoder>? logger = null)
        : this(options, windowRunner: null, logger)
    {
    }

    /// <summary>
    /// Test seam: a non-null <paramref name="windowRunner"/> replaces the ONNX session (no
    /// model file needed — only the vocabulary). Public-constructor behavior is unchanged.
    /// </summary>
    internal OnnxSpladeEncoder(
        OnnxSpladeOptions options, WindowRunner? windowRunner, ILogger<OnnxSpladeEncoder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.TopTerms < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"TopTerms ({options.TopTerms}) must be at least 1.");
        }

        // Each pass wraps its window in [CLS]/[SEP]; at least one content position must remain.
        if (options.MaxTokens <= SpecialTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxTokens ({options.MaxTokens}) must exceed the {SpecialTokensPerWindow} positions reserved for [CLS]/[SEP].");
        }

        if (windowRunner is null)
            BertOnnxPlumbing.EnsureModelFileExists(options.ModelPath);

        BertOnnxPlumbing.EnsureVocabularyFileExists(options.TokenizerVocabPath);

        _options = options;
        _logger = logger;
        _tokenizer = BertOnnxPlumbing.CreateTokenizer(options.TokenizerVocabPath);
        // One-time line count of the vocab file: cheap and lets GenerateAsync warn when the
        // model's logits dimension disagrees with the tokenizer vocabulary (a likely
        // model/vocab mix-up). Warn-only — some exports pad the vocabulary dimension.
        _tokenizerVocabularySize = CountLines(options.TokenizerVocabPath);

        if (windowRunner is not null)
        {
            _runWindow = windowRunner;
            return;
        }

        _session = new InferenceSession(options.ModelPath);
        _outputName = ResolveOutputName([.. _session.OutputMetadata.Keys], options.OutputName);
        _sendAttentionMask = _session.InputMetadata.ContainsKey("attention_mask");
        _sendTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
        _runWindow = RunWindowCore;
    }

    /// <inheritdoc />
    public async ValueTask<SparseVector> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        // No special tokens here: each pass adds its own [CLS]/[SEP]. Offsets are discarded —
        // see the class remarks for why no normalization length guard is needed. The whitespace
        // substitution is NOT about offsets, though: without it the normalizer deletes newlines
        // and this encoder weights terms the document never contained.
        var tokens = BertOnnxPlumbing.EncodeToTokens(_tokenizer, text, out _, out _);
        if (tokens.Count == 0)
            return SparseVector.Empty;

        var ids = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            ids[i] = tokens[i].Id;

        var merged = await PoolWindowedAsync(ids, cancellationToken).ConfigureAwait(false);
        return PruneTopTerms(merged, _options.TopTerms);
    }

    /// <summary>
    /// Runs the model over non-overlapping windows of <paramref name="ids"/> and merges the
    /// per-window pooled term weights by element-wise max. No overlap is used: max-pooling
    /// makes overlapping context contribute nothing an adjacent window does not already.
    /// </summary>
    private async Task<float[]> PoolWindowedAsync(int[] ids, CancellationToken cancellationToken)
    {
        var windows = TokenWindowStitcher.Windows(
            ids.Length, _options.MaxTokens - SpecialTokensPerWindow, overlap: 0);

        float[]? merged = null;
        var vocabularySize = 0;
        for (var w = 0; w < windows.Count; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = windows[w];
            // InferenceSession.Run is synchronous — keep it off the caller's thread.
            var (weights, vocab) = await Task.Run(
                () => _runWindow(ids, window.Start, window.End), cancellationToken).ConfigureAwait(false);

            if (vocab <= 0)
                throw new InvalidOperationException($"Model returned a non-positive vocabulary size ({vocab}).");

            if (weights.Length != vocab)
            {
                throw new InvalidOperationException(
                    $"Window {w} returned {weights.Length} pooled weights for vocabulary size {vocab}.");
            }

            if (merged is null)
            {
                vocabularySize = vocab;
                WarnOnceOnVocabularyMismatch(vocab);
                merged = weights;
            }
            else if (vocab != vocabularySize)
            {
                throw new InvalidOperationException(
                    $"Window {w} returned vocabulary size {vocab} but earlier windows returned {vocabularySize}.");
            }
            else
            {
                MaxMergeInPlace(merged, weights);
            }
        }

        return merged!;
    }

    /// <summary>
    /// Warns (once per instance) when the model's logits dimension disagrees with the
    /// tokenizer vocabulary file — a likely model/vocab mix-up. Not fatal: some exports pad
    /// the vocabulary dimension, so token ids remain valid indices.
    /// </summary>
    private void WarnOnceOnVocabularyMismatch(int modelVocabularySize)
    {
        if (modelVocabularySize == _tokenizerVocabularySize || _logger is null)
            return;
        if (Interlocked.Exchange(ref _vocabularyMismatchWarned, 1) != 0)
            return;

        OnnxEmbeddingsLog.SpladeVocabularySizeMismatch(
            _logger, modelVocabularySize, _tokenizerVocabularySize, _options.TokenizerVocabPath);
    }

    /// <summary>
    /// SPLADE pooling for one window: for every vocabulary term,
    /// <c>weight_v = log(1 + max(0, max over tokens of logit[token, v]))</c>. Taking the max
    /// logit first is equivalent to maxing <c>log(1 + ReLU(logit))</c> per token because
    /// <c>log1p</c> is monotonically increasing. Terms with no positive logit get weight 0.
    /// </summary>
    internal static float[] PoolWindow(ReadOnlySpan<float> logits, int vocabularySize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vocabularySize);
        if (logits.Length % vocabularySize != 0)
        {
            throw new ArgumentException(
                $"Logits length ({logits.Length}) is not a multiple of the vocabulary size ({vocabularySize}).",
                nameof(logits));
        }

        var tokenCount = logits.Length / vocabularySize;
        var weights = new float[vocabularySize];
        for (var t = 0; t < tokenCount; t++)
        {
            var rowOffset = t * vocabularySize;
            for (var v = 0; v < vocabularySize; v++)
            {
                var logit = logits[rowOffset + v];
                if (logit > weights[v])
                    weights[v] = logit; // running max of positive logits (weights start at 0)
            }
        }

        foreach (ref var weight in weights.AsSpan())
        {
            if (weight > 0f)
                weight = MathF.Log(1f + weight);
        }

        return weights;
    }

    /// <summary>Element-wise max merge of one window's weights into the accumulator.</summary>
    internal static void MaxMergeInPlace(float[] accumulator, float[] windowWeights)
    {
        ArgumentNullException.ThrowIfNull(accumulator);
        ArgumentNullException.ThrowIfNull(windowWeights);
        if (accumulator.Length != windowWeights.Length)
        {
            throw new ArgumentException(
                $"Window weights length ({windowWeights.Length}) does not match the accumulator ({accumulator.Length}).",
                nameof(windowWeights));
        }

        for (var v = 0; v < accumulator.Length; v++)
        {
            if (windowWeights[v] > accumulator[v])
                accumulator[v] = windowWeights[v];
        }
    }

    /// <summary>
    /// Keeps the <paramref name="topTerms"/> largest positive weights (ties broken by lower
    /// term id) and emits them as an ascending-index <see cref="SparseVector"/>. Zero weights
    /// never survive, so the result may hold fewer than <paramref name="topTerms"/> terms.
    /// </summary>
    internal static SparseVector PruneTopTerms(float[] weights, int topTerms)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topTerms);

        var candidates = new List<(int Index, float Weight)>();
        for (var v = 0; v < weights.Length; v++)
        {
            if (weights[v] > 0f)
                candidates.Add((v, weights[v]));
        }

        if (candidates.Count == 0)
            return SparseVector.Empty;

        if (candidates.Count > topTerms)
        {
            candidates.Sort(static (a, b) =>
            {
                var byWeight = b.Weight.CompareTo(a.Weight);
                return byWeight != 0 ? byWeight : a.Index.CompareTo(b.Index);
            });
            candidates.RemoveRange(topTerms, candidates.Count - topTerms);
            candidates.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        }

        var indices = new int[candidates.Count];
        var values = new float[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            indices[i] = candidates[i].Index;
            values[i] = candidates[i].Weight;
        }

        return new SparseVector { Indices = indices, Values = values };
    }

    /// <summary>
    /// Resolves the logits output: the preferred name when the model declares it, else the
    /// model's single output, else fails listing the model's actual outputs.
    /// </summary>
    internal static string ResolveOutputName(IReadOnlyList<string> modelOutputs, string preferredName) =>
        BertOnnxPlumbing.ResolveOutputName(modelOutputs, preferredName,
            "Set OnnxSpladeOptions.OutputName to the output holding the MLM logits [1, sequence, vocabulary].");

    /// <summary>
    /// The resolved output must be the MLM logits — rank 3 with the pass's exact sequence
    /// length. A rank-2 <c>[1, dimension]</c> shape indicates a pooled-embedding export,
    /// which cannot provide per-term logits.
    /// </summary>
    internal static void ValidateOutputShape(ReadOnlySpan<int> dimensions, int expectedSequenceLength, string outputName)
    {
        if (BertOnnxPlumbing.IsRank3WithSequenceLength(dimensions, expectedSequenceLength))
            return;

        throw new InvalidOperationException(
            $"Model output '{outputName}' has shape [{BertOnnxPlumbing.FormatShape(dimensions)}] but MLM logits " +
            $"[1, {expectedSequenceLength}, vocabulary] are required. A pooled-embedding export cannot " +
            "produce SPLADE term weights; set OnnxSpladeOptions.OutputName to the logits output or use " +
            "a model exported with its MLM head.");
    }

    /// <summary>
    /// Runs one [CLS] window [SEP] pass and pools the content-token logit rows (the two
    /// special-token rows are dropped) into per-vocabulary term weights. Pooling reads the
    /// live tensor buffer directly — the [tokens, vocabulary] logit matrix is never copied.
    /// </summary>
    private (float[] Weights, int VocabularySize) RunWindowCore(int[] ids, int start, int end)
    {
        var contentLength = end - start;
        var totalLength = contentLength + SpecialTokensPerWindow;

        var inputIds = new DenseTensor<long>([1, totalLength]);
        inputIds[0, 0] = _tokenizer.ClassificationTokenId;
        for (var i = 0; i < contentLength; i++)
            inputIds[0, i + 1] = ids[start + i];
        inputIds[0, totalLength - 1] = _tokenizer.SeparatorTokenId;

        // Only feed the inputs this model actually declares (some exports omit
        // token_type_ids or attention_mask).
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
        };
        if (_sendAttentionMask)
        {
            var attentionMask = new DenseTensor<long>([1, totalLength]);
            for (var i = 0; i < totalLength; i++)
                attentionMask[0, i] = 1;
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));
        }
        if (_sendTokenTypeIds)
        {
            // All zeros: single-segment input.
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([1, totalLength])));
        }

        using var outputs = _session!.Run(inputs, [_outputName!]);
        var tensor = outputs.First().AsTensor<float>();
        ValidateOutputShape(tensor.Dimensions, totalLength, _outputName!);
        var vocabularySize = tensor.Dimensions[2];

        // The tensor buffer is row-major [1, totalLength, vocabulary]: the content rows
        // (1..contentLength, skipping the [CLS] row) form one contiguous block. Pool them
        // in place, before the outputs are disposed.
        var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
        var weights = PoolWindow(
            dense.Buffer.Span.Slice(vocabularySize, contentLength * vocabularySize), vocabularySize);

        return (weights, vocabularySize);
    }

    /// <summary>Line count of the vocabulary file (line index == token id).</summary>
    private static int CountLines(string path)
    {
        var count = 0;
        foreach (var _ in File.ReadLines(path))
            count++;
        return count;
    }

    public void Dispose() => _session?.Dispose();
}
