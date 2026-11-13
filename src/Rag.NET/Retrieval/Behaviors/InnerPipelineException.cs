namespace Rag.NET.Retrieval.Behaviors;

/// <summary>
/// Carries an exception thrown by the rest of the retrieval pipeline out of a cache factory, so a
/// cache behaviour can tell it from a failure of the cache itself (#948).
/// </summary>
/// <remarks>
/// The embedding and result cache behaviours call <c>next</c> inside
/// <c>HybridCache.GetOrCreateAsync</c>. Their catch-all used to take every exception for a cache
/// failure and call <c>next</c> again, so a failing reranker or vector search ran twice and a
/// deliberate refusal was retried. The factory wraps what <c>next</c> throws in this marker; the
/// behaviour unwraps and rethrows the original, and only an unmarked exception degrades. A marker
/// rather than a local flag, because a concurrent caller that joined the first caller's factory
/// receives the same exception without having run the factory itself.
/// </remarks>
internal sealed class InnerPipelineException(Exception inner)
    : Exception("The retrieval pipeline failed inside a cache factory.", inner);
