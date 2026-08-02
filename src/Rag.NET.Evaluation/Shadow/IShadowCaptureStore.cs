namespace Rag.NET.Evaluation.Shadow;

/// <summary>Where captured pairs go. The seam between shadow mode and whatever persists them.</summary>
/// <remarks>
/// <para>
/// Persisting production traffic is an application decision — file, table, blob, queue — so this
/// library ships the seam and a trivial in-memory default, not a storage engine. Write-only,
/// because nothing on the capture path reads: offline analysis reads from wherever the
/// application persisted the pairs, in whatever way suits that store.
/// </para>
/// <para>
/// <b>What an implementation is signing up for:</b> a capture contains the production question and
/// retrieved document text verbatim — unless the application registered an
/// <see cref="IShadowCaptureSanitiser"/>, which runs before every save; without one, verbatim is
/// the default. Retention, encryption at rest and subject-access deletion belong to the
/// implementation either way — the seam provides none of them, and says so rather than implying
/// protection it does not provide.
/// </para>
/// </remarks>
public interface IShadowCaptureStore
{
    /// <summary>Persists one captured pair.</summary>
    /// <param name="capture">The pair to persist.</param>
    /// <param name="cancellationToken">Token to cancel the write.</param>
    /// <remarks>
    /// Called from a background consumer, never from the request path — an implementation that is
    /// slow or throws delays or loses captures, not production requests.
    /// </remarks>
    Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default);
}
