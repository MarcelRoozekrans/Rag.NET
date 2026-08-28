using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A deterministic fixture embedder whose vectors impose a <b>unique</b> ranking, for the parity
/// test's fast leg.
/// <para>
/// Text <i>i</i> of <i>n</i> maps to the 2-D unit vector at angle <c>i·δ</c>, where
/// <c>δ = π / (2(n+1))</c>. Every angle lies in <c>[0, π/2)</c>, so cosine against
/// <see cref="QueryText"/> at angle 0 is strictly decreasing in <i>i</i>: the expected ranking is
/// corpus order, and no two documents can tie.
/// </para>
/// <para>
/// <see cref="QueryText"/> alone is not enough to interrogate the pipeline. It sits at angle 0,
/// which is <i>identically</i> document 0's vector, and that coincidence makes a whole class of
/// reordering cancel out — MMR's relevance and diversity terms are equal there, so MMR is a
/// mathematical no-op on that one query and a behaviour that stopped no-opping would stay
/// invisible. <see cref="QueryTexts"/> therefore also carries one query per adjacent document pair,
/// at angles <c>j·δ + δ/3</c> for <i>j</i> = 0 … <i>n</i>-2. Each sits <c>δ/3</c> from document
/// <i>j</i>, <c>2δ/3</c> from document <i>j</i>+1, <c>4δ/3</c> from document <i>j</i>-1 and so on —
/// distinct multiples of <c>δ/3</c> throughout, so every such query still ranks the corpus without
/// a tie, from a position no document occupies.
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
    /// <exception cref="ArgumentException">
    /// <paramref name="orderedTexts"/> contains one of <see cref="QueryTexts"/>. Seeding the query
    /// vectors first and then looping over the corpus would let such a text silently overwrite a
    /// query's vector, which is the kind of degeneracy this fixture exists to make loud.
    /// </exception>
    public OrderingEmbeddingGenerator(IReadOnlyList<string> orderedTexts)
    {
        ArgumentNullException.ThrowIfNull(orderedTexts);
        ArgumentOutOfRangeException.ThrowIfZero(orderedTexts.Count, nameof(orderedTexts));

        var delta = Math.PI / (2 * (orderedTexts.Count + 1));

        // The query at angle 0 first — it is the one with a pinned, hand-checked ranking — then one
        // query per adjacent document pair, offset off every document by a third of the spacing.
        var queryTexts = new List<string>(orderedTexts.Count) { QueryText };
        var queryAngles = new List<double>(orderedTexts.Count) { 0d };
        for (var j = 0; j < orderedTexts.Count - 1; j++)
        {
            queryTexts.Add(OffsetQueryText(j));
            queryAngles.Add((j * delta) + (delta / 3));
        }

        var reserved = new HashSet<string>(queryTexts, StringComparer.Ordinal);
        foreach (var text in orderedTexts)
        {
            if (reserved.Contains(text))
            {
                throw new ArgumentException(
                    $"'{text}' is one of this fixture's query texts, so indexing it as a document " +
                    "would silently overwrite that query's vector and the parity assertion would " +
                    "compare a query against itself. Rename the corpus entry.",
                    nameof(orderedTexts));
            }
        }

        _vectorsByText = new Dictionary<string, float[]>(
            orderedTexts.Count + queryTexts.Count, StringComparer.Ordinal);

        for (var q = 0; q < queryTexts.Count; q++)
        {
            _vectorsByText[queryTexts[q]] = UnitVector(queryAngles[q]);
        }

        for (var i = 0; i < orderedTexts.Count; i++)
        {
            _vectorsByText[orderedTexts[i]] = UnitVector(i * delta);
        }

        QueryTexts = queryTexts;
    }

    /// <summary>
    /// Every query this fixture answers, <see cref="QueryText"/> first. The parity leg runs all of
    /// them: a divergence that affects only some queries is exactly what a default behaviour that
    /// stopped no-opping would produce, and a single-query leg could not see it.
    /// </summary>
    public IReadOnlyList<string> QueryTexts { get; }

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

    /// <summary>The text of the query offset off document <paramref name="index"/> by <c>δ/3</c>.</summary>
    private static string OffsetQueryText(int index) =>
        FormattableString.Invariant($"the parity query, a third past document {index}");

    private static float[] UnitVector(double angle) =>
        [(float)Math.Cos(angle), (float)Math.Sin(angle)];
}
