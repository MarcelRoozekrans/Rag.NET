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
/// <b>Only vision parsing throws this today, and the reason the others do not is a finding rather
/// than a gap.</b> Of the ingestion-path components that call a model, most — proposition and
/// resume chunking, graph entity extraction, mind-map extraction, both LLM sanitisers, and core's
/// own metadata extraction — already catch locally and degrade, so their failures never reach
/// <c>PipelineIngestor</c> and were never misclassified. Only <c>RaptorIngestionBehavior</c> and
/// <c>CommunityDetectionBehavior</c> propagate.
/// </para>
/// <para>
/// <b>Those two were wrapped, and the wrap was reverted, because a call-site <c>catch</c> cannot
/// tell a provider failure from a decorator's refusal.</b> The <c>IChatClient</c> they hold is
/// frequently not a provider at all: under the benchmark harness it is a cache opened
/// refuse-on-miss, which throws <em>instead of</em> calling the model, carrying a message that is
/// the experiment's protection. Wrapping relabelled that as "the model could not generate a
/// community report" when no model was called, and two guards that pin the refusal by type failed.
/// The same hazard applies to <see cref="BudgetExceededException"/>, which
/// <c>FallbackChatClient.IsTransient</c> already pins by type so a blown budget cannot trigger a
/// retry past the limit.
/// </para>
/// <para>
/// So a component should throw this only where the call really is a provider call and the
/// deliberate signals are let through — the vision parser rethrows both cancellation and
/// <see cref="BudgetExceededException"/> untouched before translating anything else. Extending it
/// to the two propagating behaviours needs a seam that can distinguish a decorator's refusal, which
/// does not exist yet; that is recorded on #504 rather than guessed at.
/// </para>
/// </remarks>
/// <param name="message">What failed, and where.</param>
/// <param name="innerException">The provider or SDK exception, preserved for diagnosis.</param>
public class ModelCallException(string message, Exception innerException)
    : Exception(message, innerException);
