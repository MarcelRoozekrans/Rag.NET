namespace Rag.NET.Parsers.Vision;

/// <summary>
/// Thrown when the vision model call behind <see cref="ImageDocumentParser"/> fails — the provider
/// returned an error, rate-limited the request, or answered with something its SDK could not read.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the exception a caller actually saw was unusable (#497).</b> When
/// OpenRouter returns <c>finish_reason: "error"</c> on an upstream failure, the OpenAI SDK throws
/// <c>ArgumentOutOfRangeException: Unknown ChatFinishReason value</c> from a validation helper deep
/// inside a transitive dependency. Catching that means catching an exception type indistinguishable
/// from a genuine argument bug in the caller's own code, thrown by an assembly this library only
/// depends on transitively, and named in no contract Rag.NET publishes. A second, unrelated mode
/// arrives as <c>ClientResultException: HTTP 429</c>. Both are the same thing to a caller — the
/// description could not be produced — and neither was expressible as one catch.
/// </para>
/// <para>
/// <b>It does not swallow, and that is deliberate.</b> Returning an empty description on failure
/// would ingest the image as a document with no content: retrievable, indistinguishable from an
/// image the model genuinely found nothing in, and silently wrong. A parser that fails loudly costs
/// one document; a parser that fails quietly costs the corpus's credibility. <c>PipelineIngestor</c>
/// already isolates the blast radius by catching per document and returning a failed
/// <c>Result</c> rather than aborting the batch.
/// </para>
/// <para>
/// <b>What this does not fix.</b> Through the ingestion pipeline the caller still receives
/// <c>RagError.StorageFailed</c>, because <c>RagError</c> has no case for a parse-time or
/// model-call failure and <c>PipelineIngestor</c>'s catch-all maps everything it does not recognise
/// there — even though <c>StorageFailed</c>'s own documentation scopes it to
/// <c>IVectorStore</c>/persistence operations. That is a gap in the union rather than a defect in
/// this parser, and it is tracked separately. This type helps the caller who uses
/// <see cref="ImageDocumentParser"/> directly, and gives the pipeline something meaningful to log.
/// </para>
/// <para>
/// <b>Cancellation is never reported as a failure.</b> An
/// <see cref="OperationCanceledException"/> from the caller's token propagates untouched, matching
/// the convention <c>RagError.TransportFailed</c> already documents.
/// </para>
/// </remarks>
/// <param name="message">What failed, naming the file.</param>
/// <param name="fileName">The image being described when the call failed.</param>
/// <param name="innerException">The provider or SDK exception, preserved for diagnosis.</param>
public sealed class VisionDescriptionException(
    string message, string fileName, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary>
    /// The image being described when the call failed. Carried separately from the message so a
    /// handler can act on it — retry that file, quarantine it, record it — without parsing prose.
    /// </summary>
    public string FileName { get; } = fileName;
}
