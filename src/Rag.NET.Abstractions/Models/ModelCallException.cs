namespace Rag.NET.Models;

/// <summary>
/// Thrown when a call to a language model fails — the provider returned an error, rate-limited the
/// request, or answered with something its SDK could not read. Components that call an
/// <c>IChatClient</c> translate provider failures into this so callers, and the pipeline, can tell
/// a model failure from anything else.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so the classification happens where the knowledge is (#504).</b> Only the component
/// that made the call knows a model was involved. <c>PipelineIngestor</c> catches everything and
/// cannot tell, from an exception alone, whether an <c>HttpRequestException</c> came from a chat
/// completion or from a vector store's REST API — both reach the same catch, and both can carry the
/// same status and the same words. Classifying by exception shape there would be a guess dressed as
/// a diagnosis.
/// </para>
/// <para>
/// <b>Core deliberately does not depend on provider SDKs to do this.</b> Recognising
/// <c>ClientResultException</c>, <c>RequestFailedException</c> and their equivalents would mean
/// referencing every provider's package from <c>Rag.NET</c> — worse than the problem. A marker
/// type thrown deliberately costs one <c>catch</c> at each real call site and nothing anywhere
/// else.
/// </para>
/// <para>
/// <b>Cancellation is never a model failure.</b> An <see cref="OperationCanceledException"/> raised
/// by the caller's token propagates untouched, the convention
/// <see cref="RagError.TransportFailed"/> already documents.
/// </para>
/// <para>
/// <b>Which components throw this.</b> Vision parsing, <c>EmbeddingBehavior</c>,
/// <c>SemanticChunkingStrategy</c>, <c>RaptorIngestionBehavior</c>,
/// <c>CommunityDetectionBehavior</c>, and <c>GraphEntityExtractionBehavior</c>'s entity and
/// relationship embedding — the ingestion-path model calls whose failures propagate. The rest —
/// proposition, resume and late chunking, graph entity extraction's chat call, mind-map
/// extraction, both LLM sanitisers, and core's own metadata extraction — catch locally and degrade,
/// so their failures never reach <c>PipelineIngestor</c>.
/// </para>
/// <para>
/// <b>A call-site <c>catch</c> cannot tell a provider failure from a decorator's refusal by
/// shape, which is why the RAPTOR and community-detection wraps were first reverted (#504).</b> The
/// client a component holds is frequently not a provider at all: under the benchmark harness it is
/// a cache opened refuse-on-miss, which throws <em>instead of</em> calling the model. Wrapping that
/// relabelled the experiment's protection as "the model could not generate a community report"
/// when no model was called. <see cref="CallRefusedException"/> is the seam that was missing
/// (#930): a deliberate refusal derives from it, <see cref="BudgetExceededException"/> included.
/// </para>
/// <para>
/// So every wrap follows one order: rethrow <see cref="OperationCanceledException"/>, rethrow
/// <see cref="CallRefusedException"/>, rethrow an existing <see cref="ModelCallException"/> so a
/// nested wrap does not wrap twice, and translate anything else into this, keeping the original as
/// <see cref="Exception.InnerException"/>.
/// </para>
/// </remarks>
/// <param name="message">What failed, and where.</param>
/// <param name="innerException">The provider or SDK exception, preserved for diagnosis.</param>
public class ModelCallException(string message, Exception innerException)
    : Exception(message, innerException);
