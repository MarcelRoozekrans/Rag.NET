using Microsoft.Extensions.AI;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// A deterministic embedder for <see cref="HydePipelineParityTests"/>: six corpus documents, one
/// query, and three hypotheses, each at a known angle on the unit circle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>OrderingEmbeddingGenerator</c>.</b> That fixture throws on any text outside its
/// corpus and query set — deliberately, so a silent default vector cannot make a parity test agree
/// about nothing — and hypotheses are exactly such texts. Widening it would weaken the guard
/// <c>OrderingEmbeddingGeneratorTests</c> holds over the dense parity leg, so this is a separate
/// fixture with the same discipline rather than an edit to a shared one.
/// </para>
/// <para>
/// <b>The geometry is the test's expectation.</b> Documents sit at angles 0, 1, 2, 3, 4 and 5
/// units; the query sits at 0, so plain dense retrieval ranks document 0 first. The three
/// hypotheses sit near the far end, at 3.8, 4.3 and 5.1 units, so their mean resolves to 4.399 and
/// the ranking leads with document 4 rather than document 0. <b>That displacement is what makes a
/// fallback to the query vector detectable:</b> if the shipped path ever embedded the query instead
/// of the hypotheses, the ranking would lead with document 0 and the divergence assertion fires.
/// </para>
/// <para>
/// Two dimensions, unit length, cosine similarity: <c>cos(θᵢ − θq)</c> is strictly decreasing in
/// angular distance over the range used here, so the ordering is known by construction rather than
/// by running the fixture and writing down what it did.
/// </para>
/// <para>
/// An unknown text throws. A fixture that returned a default vector for an unrecognised string
/// would let a misspelled hypothesis silently become the zero angle — which is the query's angle,
/// and therefore the one failure this fixture exists to make visible.
/// </para>
/// </remarks>
public sealed class HydeParityEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The query, at angle 0 — nearest <see cref="Corpus"/>[0].</summary>
    public const string QueryText = "the hyde parity query";

    /// <summary>The synthetic corpus, ordered by angle.</summary>
    public static readonly string[] Corpus =
    [
        "hyde parity document zero",
        "hyde parity document one",
        "hyde parity document two",
        "hyde parity document three",
        "hyde parity document four",
        "hyde parity document five",
    ];

    /// <summary>
    /// The three hypotheses the fake generator returns, at 3.8, 4.3 and 5.1 angle steps.
    /// </summary>
    /// <remarks>
    /// Three distinct angles rather than three copies of one: identical hypotheses would make the
    /// mean equal to any one of them, and mean-pooling — the step whose two implementations this
    /// parity test exists to tie together — would be unexercised.
    /// </remarks>
    public static readonly string[] Hypotheses =
    [
        "hyde parity hypothesis near three",
        "hyde parity hypothesis near four",
        "hyde parity hypothesis near five",
    ];

    private const double AngleStep = 0.35;

    /// <summary>
    /// Where each hypothesis sits, in <see cref="AngleStep"/> units — deliberately asymmetric.
    /// </summary>
    /// <remarks>
    /// The obvious choice, one hypothesis on each of documents 3, 4 and 5, is wrong in a way worth
    /// recording: the mean of three unit vectors points at the MIDDLE one, so the resultant lands
    /// exactly on document 4 and documents 3 and 5 tie at 0.35 either side. A tie makes the ranking
    /// depend on the store's sort stability rather than on the geometry, which is not something a
    /// parity test should be asserting. These three put the resultant at 4.399 steps, giving
    /// doc-4 0.990, doc-5 0.978, doc-3 0.882, doc-2 0.668 — strictly ordered, computed from the
    /// geometry rather than read off a run.
    /// </remarks>
    private static readonly double[] HypothesisAngleSteps = [3.8, 4.3, 5.1];

    private static readonly Dictionary<string, double> Angles = BuildAngles();

    /// <inheritdoc/>
    public EmbeddingGeneratorMetadata Metadata { get; } = new("hyde-parity-fixture");

    /// <inheritdoc/>
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var generated = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            if (!Angles.TryGetValue(value, out var angle))
            {
                throw new ArgumentException(
                    $"'{value}' is not a text this fixture knows. Returning a default vector here " +
                    "would place it at angle 0 — the query's angle — and a parity test would then " +
                    "agree about nothing. Add it to Corpus or Hypotheses deliberately.",
                    nameof(values));
            }

            generated.Add(new Embedding<float>(new float[]
            {
                (float)Math.Cos(angle),
                (float)Math.Sin(angle),
            }));
        }

        return Task.FromResult(generated);
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType?.IsInstanceOfType(this) is true ? this : null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    private static Dictionary<string, double> BuildAngles()
    {
        var angles = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [QueryText] = 0,
        };

        for (var i = 0; i < Corpus.Length; i++)
        {
            angles[Corpus[i]] = i * AngleStep;
        }

        for (var i = 0; i < Hypotheses.Length; i++)
        {
            angles[Hypotheses[i]] = HypothesisAngleSteps[i] * AngleStep;
        }

        return angles;
    }
}
