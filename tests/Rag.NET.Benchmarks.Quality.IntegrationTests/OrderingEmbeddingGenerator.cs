using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A deterministic fixture embedder whose vectors impose a <b>unique</b> ranking, for the parity
/// test's fast leg.
/// <para>
/// Text <i>i</i> of <i>n</i> maps to the 2-D unit vector at angle <c>i·δ</c>, where
/// <c>δ = π / (2(n+1))</c>. Every angle lies in <c>(0, π/2)</c>, so cosine against
/// <see cref="QueryText"/> at angle 0 is strictly decreasing in <i>i</i>: the expected ranking is
/// corpus order, and no two documents can tie.
/// </para>
/// <para>
/// The construction is geometric rather than hashed on purpose. A hash-derived angle is only
/// <i>probably</i> tie-free, and a fixture that is probably non-degenerate is what
/// <see cref="OrderingEmbeddingGeneratorTests"/> exists to refuse.
/// </para>
/// </summary>
/// <remarks>
/// An unknown text throws rather than returning a default vector. A silent fallback is precisely
/// the degenerate-fixture failure mode: every unrecognised text would embed identically and the
/// parity assertion would compare two copies of the same ranking.
/// </remarks>
internal sealed class OrderingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The query this fixture is built around, at angle 0 — nearest to corpus position 0.</summary>
    public const string QueryText = "the parity query";

    private readonly Dictionary<string, float[]> _vectorsByText;

    /// <summary>Creates the generator over a fixed, ordered corpus.</summary>
    /// <param name="orderedTexts">The corpus, in the order retrieval is expected to return it.</param>
    public OrderingEmbeddingGenerator(IReadOnlyList<string> orderedTexts)
    {
        ArgumentNullException.ThrowIfNull(orderedTexts);
        ArgumentOutOfRangeException.ThrowIfZero(orderedTexts.Count, nameof(orderedTexts));

        var delta = Math.PI / (2 * (orderedTexts.Count + 1));
        _vectorsByText = new Dictionary<string, float[]>(
            orderedTexts.Count + 1, StringComparer.Ordinal)
        {
            [QueryText] = [1f, 0f],
        };

        for (var i = 0; i < orderedTexts.Count; i++)
        {
            var angle = i * delta;
            _vectorsByText[orderedTexts[i]] =
                [(float)Math.Cos(angle), (float)Math.Sin(angle)];
        }
    }

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            if (!_vectorsByText.TryGetValue(value, out var vector))
            {
                throw new ArgumentException(
                    $"'{value}' is not in this fixture's corpus. Returning a default vector for an " +
                    "unknown text would make every unrecognised text embed identically, which is " +
                    "the degenerate fixture the parity test cannot detect.",
                    nameof(values));
            }

            embeddings.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(embeddings);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) is true ? this : null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
