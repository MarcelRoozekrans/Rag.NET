using Rag.NET.Abstractions;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// An <see cref="IHypotheticalDocumentGenerator"/> that returns fixed text, so
/// <see cref="HydePipelineParityTests"/> compares two retrieval paths rather than two generations.
/// </summary>
/// <remarks>
/// <para>
/// The shipped generator calls a model, and a model is not reproducible at temperature 0.8 — which
/// is the temperature every measured HyDE cell ran at. Feeding both sides identical hypotheses is
/// what leaves the pooling, the embedding and the search as the only things that can differ, and
/// those are what the parity test is about.
/// </para>
/// <para>
/// <b>It throws rather than falling back.</b> <c>HydeBehavior</c> treats a generator failure as a
/// signal to embed the plain query instead — correct in production, and silent. If this fake were
/// ever asked for more hypotheses than it holds, a lenient implementation would hand back a short
/// list, the shipped path would pool fewer vectors than the harness side, and the parity test would
/// fail with a mismatched ranking rather than the real cause. The throw names the real cause.
/// </para>
/// </remarks>
/// <param name="hypotheses">The hypotheses to return, in order.</param>
public sealed class FixedHypotheticalDocumentGenerator(IReadOnlyList<string> hypotheses)
    : IHypotheticalDocumentGenerator
{
    private readonly IReadOnlyList<string> _hypotheses =
        hypotheses ?? throw new ArgumentNullException(nameof(hypotheses));

    /// <summary>Gets how many times the pipeline asked for hypotheses.</summary>
    /// <remarks>
    /// Read by the parity test's caller only as a tripwire: zero would mean the shipped path never
    /// consulted a generator at all, which is the misregistration this fake is most likely to hide.
    /// </remarks>
    public int CallCount { get; private set; }

    /// <inheritdoc/>
    public Task<string> GenerateAsync(string query, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(_hypotheses[0]);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GenerateManyAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (count != _hypotheses.Count)
        {
            throw new InvalidOperationException(
                $"The pipeline asked for {count} hypotheses and this fake holds {_hypotheses.Count}. " +
                "Returning a shorter list would make the shipped path pool a different number of " +
                "vectors from the harness row, and the parity test would report a ranking mismatch " +
                "instead of this — a wrong answer to a question nobody asked. Set " +
                "HydeOptions.HypothesisCount to match the fixture.");
        }

        return Task.FromResult(_hypotheses);
    }
}
