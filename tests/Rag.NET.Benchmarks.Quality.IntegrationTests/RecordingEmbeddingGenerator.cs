using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// The <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> the Semantic Kernel entrant hands to
/// <c>VectorStoreCollectionOptions.EmbeddingGenerator</c>: a pass-through to the pinned embedder
/// that <b>records every text the library asks to embed</b>.
/// <para>
/// The recording is the entrant's evidence that its row measured retrieval and not embedders. The
/// comparison's one matched element is the embedder (design §2), and the embedding calls happen
/// inside Semantic Kernel's own upsert and search paths where nothing external can see them — so
/// without a record, "the pinned model embedded exactly the corpus texts and the query texts,
/// unmodified" would be an assumption. With it,
/// <see cref="BeirSemanticKernelDefaultsTests"/> asserts that sentence against the texts it
/// supplied, and the identity check compares a known string's vector from this path against
/// <c>OnnxEmbeddingGenerator</c> called directly.
/// </para>
/// <para>
/// The source is a delegate rather than the generator itself so the measurement can route through
/// <see cref="EmbeddingCache"/> exactly as every other measurement in this project does — the
/// cache stores the pinned generator's outputs verbatim, keyed on the model identity and the exact
/// text, so a cached vector is the same vector. The recorder hands vectors back <b>unmodified</b>;
/// re-pooling or re-normalising here would not throw, it would quietly move the number.
/// </para>
/// </summary>
public sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> _source;
    private readonly List<string> _requestedTexts = [];
    private readonly List<float[]> _producedVectors = [];
    private readonly Lock _sync = new();

    /// <summary>Creates a recorder over <paramref name="source"/>.</summary>
    /// <param name="source">
    /// What actually embeds: normally <c>BeirHarness.EmbedAsync</c> over the pinned generator and
    /// the embedding cache; the identity check passes the generator alone so its comparison cannot
    /// be satisfied by a cache hit.
    /// </param>
    public RecordingEmbeddingGenerator(
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>
    /// Gets every text embedding has been requested for, in request order, exactly as received.
    /// </summary>
    public IReadOnlyList<string> RequestedTexts
    {
        get
        {
            lock (_sync)
            {
                return _requestedTexts.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets every vector handed back, in the same order as <see cref="RequestedTexts"/> — the
    /// vectors exactly as the library received them, which is what the identity check compares
    /// against the pinned generator called directly.
    /// </summary>
    public IReadOnlyList<float[]> ProducedVectors
    {
        get
        {
            lock (_sync)
            {
                return _producedVectors.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var texts = new List<string>();
        foreach (var value in values)
        {
            texts.Add(value);
        }

        lock (_sync)
        {
            _requestedTexts.AddRange(texts);
        }

        var vectors = await _source(texts, cancellationToken);
        if (vectors.Count != texts.Count)
        {
            throw new InvalidOperationException(
                $"The embedding source returned {vectors.Count} vectors for {texts.Count} texts. " +
                "A short batch would silently pair texts with their neighbours' vectors.");
        }

        lock (_sync)
        {
            _producedVectors.AddRange(vectors);
        }

        var embeddings = new Embedding<float>[vectors.Count];
        for (var i = 0; i < vectors.Count; i++)
        {
            embeddings[i] = new Embedding<float>(vectors[i]);
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing owned: the pinned generator's lifetime belongs to the test that created it.
    }
}
