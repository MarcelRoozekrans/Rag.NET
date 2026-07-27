using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> returning scripted vectors, and
/// recording what it was asked to embed.
/// </summary>
/// <remarks>
/// Hand-written rather than NSubstitute because Answer Relevance embeds the original question and
/// the synthetic questions in one batch, and the assertions are about which text got which
/// vector — plus, for the noncommittal path, that nothing was embedded at all.
/// </remarks>
internal sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Func<string, float[]> _vectorFor;
    private readonly Lock _gate = new();
    private readonly List<string> _inputs = [];
    private int _callCount;

    /// <summary>Creates a generator that maps each text to a vector.</summary>
    /// <param name="vectorFor">Chooses the vector for one text.</param>
    public StubEmbeddingGenerator(Func<string, float[]> vectorFor) => _vectorFor = vectorFor;

    /// <summary>Creates a generator that returns the same vector for every text.</summary>
    /// <param name="vector">The vector every text embeds to.</param>
    public StubEmbeddingGenerator(float[] vector)
        : this(_ => vector)
    {
    }

    /// <summary>Every text seen, in the order it was requested.</summary>
    public IReadOnlyList<string> Inputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _inputs];
            }
        }
    }

    /// <summary>How many batches were requested. Zero proves nothing was embedded.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>When set, returns this many vectors regardless of how many texts were given.</summary>
    public int? TruncateTo { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);

        var generated = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            lock (_gate)
            {
                _inputs.Add(value);
            }

            if (TruncateTo is { } limit && generated.Count >= limit)
                continue;

            generated.Add(new Embedding<float>(_vectorFor(value)));
        }

        return Task.FromResult(generated);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release: the scripted vectors are plain arrays.
    }
}
