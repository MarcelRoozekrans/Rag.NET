using Microsoft.Extensions.AI;
using Xunit;

namespace Rag.NET.Embeddings.Onnx.Tests;

/// <summary>
/// Exercises <see cref="OnnxEmbeddingGenerator.GenerateAsync"/> through the internal batch-runner
/// seam (no ONNX model needed — only a tiny temp WordPiece vocab), so the padding behaviour is
/// pinned on the real code path rather than only on the pooling function.
/// </summary>
public sealed class OnnxEmbeddingGeneratorTests : IDisposable
{
    /// <summary>Value the fake runner writes into every padded row: nothing may reach a vector.</summary>
    private const float PadRowValue = 1000f;

    private const string LongText = "alpha bravo charlie delta";

    private readonly string _vocabPath;

    public OnnxEmbeddingGeneratorTests()
    {
        _vocabPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".vocab.txt");
        // "alphabravo" and "##charlie" are merge bait: they exist only so that a separator the
        // normalizer DELETED produces ids of its own rather than an indistinguishable [UNK].
        File.WriteAllLines(_vocabPath,
            [
                "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]",
                "alpha", "bravo", "charlie", "delta", "alphabravo", "##charlie",
            ]);
    }

    public void Dispose() => File.Delete(_vocabPath);

    private OnnxEmbeddingGenerator CreateSut(
        OnnxEmbeddingGenerator.BatchRunner runner, int maxTokens = 256, int batchSize = 16) =>
        new(new OnnxEmbeddingOptions
        {
            ModelPath = "unused/all-MiniLM-L6-v2.onnx", // never touched: the seam bypasses the session
            TokenizerVocabPath = _vocabPath,
            MaxTokens = maxTokens,
            BatchSize = batchSize,
        }, runner);

    /// <summary>
    /// A stand-in for a correctly masked transformer: a real token's row depends only on its id,
    /// so batching cannot change it, while every PADDED row carries <see cref="PadRowValue"/>.
    /// Any pooling that counts padding therefore produces a visibly different vector.
    /// </summary>
    private static OnnxEmbeddingGenerator.BatchRunner IdBasedRunner(int dimension = 3, Action? onCall = null) =>
        (inputIds, attentionMask, batchSize, sequenceLength) =>
        {
            onCall?.Invoke();
            var hidden = new float[batchSize * sequenceLength * dimension];
            for (var position = 0; position < inputIds.Length; position++)
            {
                var offset = position * dimension;
                for (var d = 0; d < dimension; d++)
                {
                    hidden[offset + d] = attentionMask[position] == 0
                        ? PadRowValue
                        : inputIds[position] * (d + 1);
                }
            }

            return (hidden, dimension);
        };

    [Fact]
    public async Task GenerateAsync_ShortTextBatchedWithALongOne_EmbedsExactlyAsItDoesAlone()
    {
        // The whole point of masking the padding. "alpha" is 3 tokens ([CLS] alpha [SEP]) and the
        // long text is 6, so batching them pads "alpha" with three rows of PadRowValue.
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(IdBasedRunner());

        var alone = await sut.GenerateAsync(["alpha"], cancellationToken: ct);
        var batched = await sut.GenerateAsync(["alpha", LongText], cancellationToken: ct);

        Assert.Equal(alone[0].Vector.ToArray(), batched[0].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_PaddedRows_AreNotWhatPadInclusivePoolingWouldProduce()
    {
        // Guards the test above from being vacuous: if padding WERE pooled in, the batched vector
        // would be this instead. A fixture where both answers agree would be watching nothing.
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(IdBasedRunner());

        var batched = await sut.GenerateAsync(["alpha", LongText], cancellationToken: ct);

        // [CLS]=2, alpha=5, [SEP]=3 then three pad rows; dimension d contributes id*(d+1).
        var padInclusive = new float[3];
        for (var d = 0; d < 3; d++)
            padInclusive[d] = (((2f + 5f + 3f) * (d + 1)) + (3f * PadRowValue)) / 6f;
        OnnxEmbeddingGenerator.L2NormalizeInPlace(padInclusive);

        Assert.NotEqual(padInclusive, batched[0].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BatchSizeOne_MatchesABatchedRun()
    {
        // Same texts, different pass grouping — hence different padding — must agree.
        var ct = TestContext.Current.CancellationToken;
        var oneAtATime = CreateSut(IdBasedRunner(), batchSize: 1);
        var batched = CreateSut(IdBasedRunner(), batchSize: 8);

        var sequential = await oneAtATime.GenerateAsync(["alpha", "alpha bravo", LongText], cancellationToken: ct);
        var together = await batched.GenerateAsync(["alpha", "alpha bravo", LongText], cancellationToken: ct);

        for (var i = 0; i < sequential.Count; i++)
            Assert.Equal(sequential[i].Vector.ToArray(), together[i].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_BatchSizeOne_RunsOnePassPerText()
    {
        var calls = 0;
        var sut = CreateSut(IdBasedRunner(onCall: () => calls++), batchSize: 1);

        await sut.GenerateAsync(["alpha", "bravo", "charlie"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GenerateAsync_MoreTextsThanTheBatchSize_SplitsIntoPasses()
    {
        var calls = 0;
        var sut = CreateSut(IdBasedRunner(onCall: () => calls++), batchSize: 2);

        var result = await sut.GenerateAsync(
            ["alpha", "bravo", "charlie", "delta", "alpha bravo"],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, calls); // 2 + 2 + 1
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsUnitLengthVectorsInInputOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(IdBasedRunner());

        var result = await sut.GenerateAsync(["alpha", "bravo", LongText], cancellationToken: ct);

        Assert.Equal(3, result.Count);
        Assert.All(result, embedding =>
        {
            Assert.Equal(3, embedding.Vector.Length);

            double normSquared = 0;
            foreach (ref readonly var value in embedding.Vector.Span)
                normSquared += (double)value * value;
            Assert.Equal(1.0, Math.Sqrt(normSquared), 1e-6);
        });

        // Different texts must not collapse to the same vector.
        Assert.NotEqual(result[0].Vector.ToArray(), result[1].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_EmptyInput_ReturnsNoEmbeddingsWithoutRunningTheModel()
    {
        var calls = 0;
        var sut = CreateSut(IdBasedRunner(onCall: () => calls++));

        var result = await sut.GenerateAsync([], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, calls);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateAsync_WhitespaceOnlyText_StillProducesAVector()
    {
        // No content tokens, so the sequence is [CLS] [SEP]: a real (if uninformative) vector,
        // never a zero-length one that a vector store would reject at insert time.
        var sut = CreateSut(IdBasedRunner());

        var result = await sut.GenerateAsync(["   "], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result[0].Vector.Length);
    }

    [Fact]
    public async Task GenerateAsync_WrapsEachSequenceInClassificationAndSeparatorTokens()
    {
        long[]? seenIds = null;
        long[]? seenMask = null;
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) =>
        {
            seenIds = inputIds;
            seenMask = attentionMask;
            return (new float[batchSize * sequenceLength * 2], 2);
        });

        await sut.GenerateAsync(["alpha", LongText], cancellationToken: TestContext.Current.CancellationToken);

        // Line index == token id in the temp vocab: [CLS]=2, [SEP]=3, [PAD]=0, alpha..delta=5..8.
        Assert.NotNull(seenIds);
        Assert.NotNull(seenMask);
        Assert.Equal([2, 5, 3, 0, 0, 0, 2, 5, 6, 7, 8, 3], seenIds);
        Assert.Equal([1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1], seenMask);
    }

    /// <summary>
    /// The newline/tab defect pinned on THIS generator's own call site. It pools internally and
    /// exposes no offsets, so <see cref="OnnxTokenEmbeddingGenerator"/>'s normalization guard never
    /// runs here and a merged word raises nothing at all — the ids are the only evidence there is,
    /// which is why the assertion is on them rather than on an exception. Reverting
    /// <c>Encode</c> to call the tokenizer directly (bypassing
    /// <see cref="BertOnnxPlumbing.EncodeToTokens"/>) lets the normalizer delete the <c>\n</c> and
    /// the <c>\t</c>, and the three words arrive as <c>alphabravo | ##charlie</c> — a vector for a
    /// document that never contained those words.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_MultiLineTabbedText_DoesNotMergeTheWordsAcrossTheSeparators()
    {
        long[]? seenIds = null;
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) =>
        {
            seenIds = inputIds;
            return (new float[batchSize * sequenceLength * 2], 2);
        });

        await sut.GenerateAsync(
            ["alpha\nbravo\tcharlie"], cancellationToken: TestContext.Current.CancellationToken);

        // [CLS]=2 then three separate words alpha=5, bravo=6, charlie=7 then [SEP]=3. Never the
        // merge-bait 9 or 10.
        Assert.NotNull(seenIds);
        Assert.Equal([2, 5, 6, 7, 3], seenIds);
    }

    [Fact]
    public async Task GenerateAsync_TextLongerThanMaxTokens_IsTruncatedRatherThanWindowed()
    {
        // MaxTokens 4 → 2 content positions. Unlike OnnxTokenEmbeddingGenerator this makes ONE
        // pass over the truncated head; stitching windows into a single pooled vector would be a
        // different quantity.
        var calls = 0;
        long[]? seenIds = null;
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) =>
        {
            calls++;
            seenIds = inputIds;
            return (new float[batchSize * sequenceLength * 2], 2);
        }, maxTokens: 4);

        await sut.GenerateAsync([LongText], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.NotNull(seenIds);
        Assert.Equal([2, 5, 6, 3], seenIds);
    }

    [Fact]
    public async Task GenerateAsync_CancellationBetweenBatches_StopsFurtherPasses()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) =>
        {
            calls++;
            cts.Cancel(); // cancel during the first pass — the loop must stop before pass 2
            return (new float[batchSize * sequenceLength * 2], 2);
        }, batchSize: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.GenerateAsync(["alpha", "bravo"], cancellationToken: cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GenerateAsync_NonPositiveDimension_Throws()
    {
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) => ([], 0));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync(["alpha"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("dimension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_HiddenStatesOfTheWrongLength_Throws()
    {
        var sut = CreateSut((inputIds, attentionMask, batchSize, sequenceLength) =>
            (new float[batchSize * sequenceLength], 2)); // half of what dimension 2 needs

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sut.GenerateAsync(["alpha"], cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("hidden values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_DimensionsRequestTheModelCannotSatisfy_ThrowsNotSupported()
    {
        var sut = CreateSut(IdBasedRunner());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await sut.GenerateAsync(
                ["alpha"],
                new EmbeddingGenerationOptions { Dimensions = 512 },
                TestContext.Current.CancellationToken));

        Assert.Contains("512", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_DimensionsRequestMatchingTheModel_Succeeds()
    {
        var sut = CreateSut(IdBasedRunner());

        var result = await sut.GenerateAsync(
            ["alpha"],
            new EmbeddingGenerationOptions { Dimensions = 3 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result[0].Vector.Length);
    }

    [Fact]
    public async Task GenerateAsync_NullValues_Throws()
    {
        var sut = CreateSut(IdBasedRunner());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sut.GenerateAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Model identity: what EmbeddingModelIdentity reads ────────────────────

    [Fact]
    public void GetService_ReturnsMetadataWithProviderAndModelId()
    {
        // Rag.NET's EmbeddingModelIdentity resolves "{ProviderName}/{DefaultModelId}" from exactly
        // this service, and treats an empty DefaultModelId as unresolvable — which silently
        // disables embedding versioning, so stale vectors never get re-indexed.
        using var sut = CreateSut(IdBasedRunner());

        var metadata = sut.GetService(typeof(EmbeddingGeneratorMetadata)) as EmbeddingGeneratorMetadata;

        Assert.NotNull(metadata);
        Assert.Equal("onnx", metadata.ProviderName);
        Assert.Equal("all-MiniLM-L6-v2", metadata.DefaultModelId);
    }

    [Fact]
    public void GetService_KeyedRequest_ReturnsNull()
    {
        using var sut = CreateSut(IdBasedRunner());

        Assert.Null(sut.GetService(typeof(EmbeddingGeneratorMetadata), "some-key"));
    }

    [Fact]
    public void GetService_OwnType_ReturnsItself()
    {
        using var sut = CreateSut(IdBasedRunner());

        Assert.Same(sut, sut.GetService(typeof(OnnxEmbeddingGenerator)));
    }

    [Fact]
    public void GetService_UnrelatedType_ReturnsNull()
    {
        using var sut = CreateSut(IdBasedRunner());

        Assert.Null(sut.GetService(typeof(string)));
    }

    [Fact]
    public async Task GenerateAsync_StampsTheModelIdOnEveryEmbedding()
    {
        var sut = CreateSut(IdBasedRunner());

        var result = await sut.GenerateAsync(["alpha", "bravo"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(result, embedding => Assert.Equal("all-MiniLM-L6-v2", embedding.ModelId));
    }

    [Fact]
    public void ResolveModelId_ExplicitModelId_WinsOverTheFileName()
    {
        var id = OnnxEmbeddingGenerator.ResolveModelId(new OnnxEmbeddingOptions
        {
            ModelPath = "models/model.onnx",
            TokenizerVocabPath = "models/vocab.txt",
            ModelId = "all-MiniLM-L6-v2",
        });

        Assert.Equal("all-MiniLM-L6-v2", id);
    }

    [Fact]
    public void ResolveModelId_NoExplicitId_DerivesFromTheModelFileName()
    {
        var id = OnnxEmbeddingGenerator.ResolveModelId(new OnnxEmbeddingOptions
        {
            ModelPath = "/models/onnx/bge-small-en-v1.5.onnx",
            TokenizerVocabPath = "vocab.txt",
        });

        Assert.Equal("bge-small-en-v1.5", id);
    }

    [Fact]
    public void ResolveModelId_NoFileNameToDeriveFrom_ReturnsNullRatherThanAPlaceholder()
    {
        // A placeholder identity would be worse than none: every model would stamp the same
        // string, so a model swap would not read as a change.
        var id = OnnxEmbeddingGenerator.ResolveModelId(new OnnxEmbeddingOptions
        {
            ModelPath = "",
            TokenizerVocabPath = "vocab.txt",
        });

        Assert.Null(id);
    }

    // ── Construction guards ─────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new OnnxEmbeddingGenerator(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Constructor_MaxTokensLeavesNoContentPositions_Throws(int maxTokens)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateSut(IdBasedRunner(), maxTokens: maxTokens));

        Assert.Contains("MaxTokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_BatchSizeBelowOne_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => CreateSut(IdBasedRunner(), batchSize: 0));

        Assert.Contains("BatchSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingVocabulary_ThrowsEvenThroughTheSeam()
    {
        // The seam replaces the session, never the tokenizer: a missing vocab is still fatal.
        Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingGenerator(
            new OnnxEmbeddingOptions
            {
                ModelPath = "unused/model.onnx",
                TokenizerVocabPath = "nonexistent/vocab.txt",
            },
            IdBasedRunner()));
    }

    [Fact]
    public void Constructor_MissingModelFile_ThrowsWithoutTheSeam()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingGenerator(new OnnxEmbeddingOptions
        {
            ModelPath = "nonexistent/model.onnx",
            TokenizerVocabPath = _vocabPath,
        }));

        Assert.Contains("nonexistent/model.onnx", ex.Message, StringComparison.Ordinal);
    }
}
