namespace Rag.NET.Evaluation.Shadow;

/// <summary>
/// Scrubs a captured pair before it reaches the <see cref="IShadowCaptureStore"/> — the seam
/// design §5 requires, because captures hold production data verbatim: the user's question
/// exactly as typed, and the retrieved document text of both variants.
/// </summary>
/// <remarks>
/// <para>
/// <b>By default there is no sanitiser and everything persists verbatim.</b> That default is a
/// deliberate, documented choice rather than one to discover in an audit: what scrubbing is
/// appropriate is an application decision, and this package takes no dependency on
/// <c>Rag.NET.Security</c> — wrap its <c>PiiChunkSanitiser</c>, <c>RegexChunkSanitiser</c> or
/// <c>LlmPiiChunkSanitiser</c> in an implementation of this interface, or write your own, and
/// register it; <c>UseShadow</c> picks it up.
/// </para>
/// <para>
/// <b>Failure loses the capture, never the protection.</b> An implementation that throws — or
/// returns <see langword="null"/> — costs exactly that capture: it is counted, recorded as a
/// persistence failure (keyed, in process memory only, by the unsanitised question), and the
/// consumer moves on. Nothing an implementation does can cause an unsanitised capture to be
/// persisted, which is the only safe direction for this seam to fail in.
/// </para>
/// <para>
/// Runs on the background consumer, one capture at a time, never on the request path — a slow
/// sanitiser (an LLM-backed one, say) delays captures, not callers. What it does not do also
/// matters: retention, encryption at rest and subject-access deletion belong to whoever
/// implements the store.
/// </para>
/// </remarks>
public interface IShadowCaptureSanitiser
{
    /// <summary>Returns the capture as it may be persisted.</summary>
    /// <param name="capture">The captured pair, payloads verbatim.</param>
    /// <param name="cancellationToken">
    /// The consumer's drain deadline: at shutdown a sanitiser observing it ends promptly, and
    /// one that ignores it is bounded from outside anyway.
    /// </param>
    /// <returns>The scrubbed capture; never <see langword="null"/>.</returns>
    Task<ShadowCapture> SanitiseAsync(ShadowCapture capture, CancellationToken cancellationToken = default);
}
