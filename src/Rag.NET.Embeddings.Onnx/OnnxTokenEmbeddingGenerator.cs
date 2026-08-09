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
/// (<see cref="TokenWindowStitcher.Windows"/>), each window runs through the model, and each
/// window's rows are copied into one preallocated matrix as the pass completes — rows in
/// overlap regions are overwritten by the LATER window (more right-context, same policy as
/// <see cref="TokenWindowStitcher.Stitch"/>).
/// <para>
/// Special tokens: the text is tokenized WITHOUT special tokens, so
/// <see cref="TokenEmbeddingResult.TokenOffsets"/> maps 1:1 to content tokens. Each model pass
/// wraps its window in [CLS] … [SEP] (as BERT-style models expect) and the two special-token
/// rows are dropped from the output — offsets and matrix rows stay aligned.
/// Offsets index the tokenizer's normalized text; standard BERT normalization (lowercasing,
/// whitespace cleanup) is position-preserving. Newlines, tabs and CRs are NOT: the normalizer
/// deletes them, so <see cref="BertOnnxPlumbing.SubstituteWhitespace"/> replaces each with a
/// space first — one character for one, which keeps every offset valid and stops the words
/// either side of a line break merging. Inputs where normalization still CHANGES the text
/// length (CJK, which grows, and NFD-decomposed accents, which shrink) are rejected with
/// <see cref="InvalidOperationException"/> rather than returning silently misaligned offsets —
/// late chunking treats that as a generator failure and falls back to unembedded chunks.
/// </para>
/// <para>
/// Model I/O: <c>input_ids</c> is always supplied; <c>attention_mask</c> and
/// <c>token_type_ids</c> are supplied only when the model declares them. The token-level
/// output is resolved by <see cref="OnnxTokenEmbeddingOptions.OutputName"/> (falling back to
/// the model's single output) and validated to be rank 3 <c>[1, sequence, dimension]</c>.
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
    /// <summary>[CLS] and [SEP] positions reserved per model pass.</summary>
    private const int SpecialTokensPerWindow = 2;

    /// <summary>
    /// One model pass over <c>ids[start..end)</c> returning the content-token rows (row-major)
    /// and the embedding dimension. Internal seam so tests can exercise
    /// <see cref="GenerateAsync"/> without an ONNX model.
    /// </summary>
    internal delegate (float[] Matrix, int Dimension) WindowRunner(int[] ids, int start, int end);

    private readonly InferenceSession? _session;
    private readonly BertTokenizer _tokenizer;
    private readonly OnnxTokenEmbeddingOptions _options;
    private readonly WindowRunner _runWindow;
    private readonly string? _outputName;
    private readonly bool _sendAttentionMask;
    private readonly bool _sendTokenTypeIds;

    public OnnxTokenEmbeddingGenerator(OnnxTokenEmbeddingOptions options)
        : this(options, windowRunner: null)
    {
    }

    /// <summary>
    /// Test seam: a non-null <paramref name="windowRunner"/> replaces the ONNX session (no
    /// model file needed — only the vocabulary). Public-constructor behavior is unchanged.
    /// </summary>
    internal OnnxTokenEmbeddingGenerator(OnnxTokenEmbeddingOptions options, WindowRunner? windowRunner)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WindowOverlapTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"WindowOverlapTokens ({options.WindowOverlapTokens}) must not be negative.");
        }

        // Each pass wraps its window in [CLS]/[SEP], so the usable content budget per window
        // is MaxTokens - 2 — and that budget must still exceed the overlap.
        if (options.MaxTokens - SpecialTokensPerWindow <= options.WindowOverlapTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                $"MaxTokens ({options.MaxTokens}) must exceed WindowOverlapTokens ({options.WindowOverlapTokens}) " +
                $"plus the {SpecialTokensPerWindow} positions reserved for [CLS]/[SEP].");
        }

        if (windowRunner is null)
            BertOnnxPlumbing.EnsureModelFileExists(options.ModelPath);

        BertOnnxPlumbing.EnsureVocabularyFileExists(options.TokenizerVocabPath);

        _options = options;
        _tokenizer = BertOnnxPlumbing.CreateTokenizer(options.TokenizerVocabPath);

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
    public int MaxTokens => _options.MaxTokens;

    /// <inheritdoc />
    public async ValueTask<TokenEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        // No special tokens here: one EncodedToken per content token, offsets into the text.
        // BertOnnxPlumbing substitutes a space for every newline, tab and CR first — the
        // normalizer deletes them, which merges the words either side. The substitution is
        // length-preserving, so every offset into encodedText is the same offset into text.
        var tokens = BertOnnxPlumbing.EncodeToTokens(_tokenizer, text, out var encodedText, out var normalizedText);
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

        // Offsets index the NORMALIZED text; the ITokenEmbeddingGenerator contract promises
        // spans into the ORIGINAL input. Fail loudly when normalization changed the length
        // (0.22.0 returns a null normalizedText even when it normalized, so re-run the
        // normalizer to compare).
        ThrowIfNormalizationChangedLength(
            encodedText.Length, normalizedText ?? _tokenizer.Normalizer?.Normalize(encodedText));

        var offsets = new (int Start, int End)[tokens.Count];
        var ids = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            offsets[i] = (tokens[i].Offset.Start.Value, tokens[i].Offset.End.Value);
            ids[i] = tokens[i].Id;
        }

        var (stitched, dimension) = await RunWindowedAsync(ids, cancellationToken).ConfigureAwait(false);

        return new TokenEmbeddingResult
        {
            Embeddings = stitched,
            Dimension = dimension,
            TokenOffsets = offsets,
        };
    }

    /// <summary>
    /// Runs the model over overlapping windows of <paramref name="ids"/> and stitches the
    /// per-window matrices incrementally: the full matrix is allocated once and each window's
    /// rows are copied in as its pass completes; later windows overwrite overlap rows (same
    /// policy as <see cref="TokenWindowStitcher.Stitch"/>).
    /// </summary>
    private async Task<(float[] Stitched, int Dimension)> RunWindowedAsync(int[] ids, CancellationToken cancellationToken)
    {
        // Reserve the [CLS]/[SEP] positions so a full window + special tokens never exceeds
        // the model's MaxTokens sequence limit.
        var windows = TokenWindowStitcher.Windows(
            ids.Length, _options.MaxTokens - SpecialTokensPerWindow, _options.WindowOverlapTokens);

        // Guards the invariant the incremental CopyWindow loop below cannot see on its own:
        // every row of the stitched matrix must be written by some window, or it silently stays
        // zero-filled. Windows() satisfies this by construction today; checking it here keeps
        // "by construction" from decaying into "by assumption" — before this call the check
        // lived behind Stitch, which only tests reach (issue #90).
        TokenWindowStitcher.ValidateCoverage(windows, ids.Length);

        float[]? stitched = null;
        var dimension = 0;
        for (var w = 0; w < windows.Count; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = windows[w];
            // InferenceSession.Run is synchronous — keep it off the caller's thread.
            var (matrix, dim) = await Task.Run(
                () => _runWindow(ids, window.Start, window.End), cancellationToken).ConfigureAwait(false);

            if (stitched is null)
            {
                if (dim <= 0)
                    throw new InvalidOperationException($"Model returned a non-positive embedding dimension ({dim}).");

                dimension = dim;
                stitched = new float[ids.Length * dimension];
            }
            else if (dim != dimension)
            {
                throw new InvalidOperationException(
                    $"Window {w} returned embedding dimension {dim} but earlier windows returned {dimension}.");
            }

            TokenWindowStitcher.CopyWindow(stitched, matrix, window.Start, window.End, dimension);
        }

        return (stitched!, dimension);
    }

    /// <summary>
    /// Rejects inputs whose tokenizer normalization changed the text length: token offsets
    /// index the normalized text and can no longer be mapped to the original input.
    /// </summary>
    /// <remarks>
    /// The message names the DIRECTION the length moved and the cause that direction implies,
    /// because the remaining causes are different problems with different answers: text that GREW
    /// is CJK (unsupported), text that SHRANK is NFD or a control character (both fixable by the
    /// caller). It used to advise stripping control characters without qualification, which now
    /// misleads for the commonest members of that class — <c>\n</c>, <c>\t</c> and <c>\r</c> are
    /// substituted with a space by <see cref="BertOnnxPlumbing.SubstituteWhitespace"/> before this
    /// point and can no longer be the cause, while the rarer control characters still can.
    /// </remarks>
    internal static void ThrowIfNormalizationChangedLength(int originalLength, string? normalized)
    {
        // A null normalized text means there is nothing to compare, not that nothing changed:
        // the caller has already re-run the normalizer to get a non-null one where it could.
        if (normalized is null || normalized.Length == originalLength)
            return;

        var grew = normalized.Length > originalLength;
        throw new InvalidOperationException(
            $"Tokenizer normalization {(grew ? "grew" : "shrank")} the text length from {originalLength} " +
            $"to {normalized.Length}; token offsets index the normalized text and cannot be mapped back " +
            $"to the original input. {LikelyCauseOfLengthChange(grew)}");
    }

    /// <summary>
    /// Names the likely cause of a length change in the given direction, with the remedy that
    /// applies to it — the two directions have measurably different causes and only one of them is
    /// something the caller can fix. Both figures are measured, and asserted by
    /// <c>NormalizationGuardTests</c>' real-tokenizer cases: CJK grows (<c>"日本語 text"</c>,
    /// 8 → 14) and NFD shrinks (<c>"cafe" + U+0301 + " test"</c>, 10 → 9).
    /// </summary>
    private static string LikelyCauseOfLengthChange(bool grew) => grew
        ? "The text most likely contains CJK: the normalizer inserts a space either side of every " +
          "Chinese, Japanese and Korean ideograph, so the text grows. No length-preserving rewrite " +
          "exists for that, so token-level embedding (and therefore late chunking) does not support " +
          "CJK text."
        : "The text is most likely NFD-decomposed — the form macOS filesystems produce — whose " +
          "combining marks the normalizer strips, so the text shrinks; pre-normalize it to NFC " +
          "(string.Normalize()) and it will be accepted. A remaining control character is deleted " +
          "the same way, so removing those helps too — but newlines, tabs and CRs cannot be the " +
          "cause: they are substituted with a space before this point.";

    /// <summary>
    /// Resolves the token-level output: the preferred name when the model declares it, else
    /// the model's single output, else fails listing the model's actual outputs.
    /// </summary>
    internal static string ResolveOutputName(IReadOnlyList<string> modelOutputs, string preferredName) =>
        BertOnnxPlumbing.ResolveOutputName(modelOutputs, preferredName,
            "Set OnnxTokenEmbeddingOptions.OutputName to the output holding the token-level hidden states [1, sequence, dimension].");

    /// <summary>
    /// The resolved output must be the token-level last hidden state — rank 3 with the pass's
    /// exact sequence length. A rank-2 <c>[1, dimension]</c> shape indicates a pooled-embedding
    /// export, which cannot provide per-token vectors.
    /// </summary>
    internal static void ValidateOutputShape(ReadOnlySpan<int> dimensions, int expectedSequenceLength, string outputName)
    {
        if (BertOnnxPlumbing.IsRank3WithSequenceLength(dimensions, expectedSequenceLength))
            return;

        throw new InvalidOperationException(
            $"Model output '{outputName}' has shape [{BertOnnxPlumbing.FormatShape(dimensions)}] but a token-level last hidden state " +
            $"[1, {expectedSequenceLength}, dimension] is required. A [1, dimension] shape indicates a " +
            "pooled-embedding export; set OnnxTokenEmbeddingOptions.OutputName to the token-level output " +
            "or use a model exported with token-level hidden states.");
    }

    /// <summary>
    /// Runs one [CLS] window [SEP] pass and returns the content-token rows (the two
    /// special-token rows are dropped) as a row-major matrix plus the model dimension.
    /// </summary>
    private (float[] Matrix, int Dimension) RunWindowCore(int[] ids, int start, int end)
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
        var dimension = tensor.Dimensions[2];

        // The tensor buffer is row-major [1, totalLength, dimension]: the content rows
        // (1..contentLength, skipping the [CLS] row) form one contiguous block.
        var dense = tensor as DenseTensor<float> ?? tensor.ToDenseTensor();
        var matrix = new float[contentLength * dimension];
        dense.Buffer.Span.Slice(dimension, matrix.Length).CopyTo(matrix);

        return (matrix, dimension);
    }

    public void Dispose() => _session?.Dispose();
}
