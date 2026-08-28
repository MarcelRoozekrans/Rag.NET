using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.AnswerGeneration;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using ZeroAlloc.Results;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Builds the answer engine each engine arm generates with, over the harness's own answering
/// client.
/// <para>
/// Every engine receives the shared <c>CachedGraphRagClient</c> and builds its own prompts. The
/// answer cache is keyed on prompt text, so each engine's prompts are new keys and no existing
/// entry is touched — which is what keeps <c>dense</c>, <c>global</c> and the RAPTOR arms
/// reproducible while these arms are added.
/// </para>
/// </summary>
internal static class AnswerEngineArms
{
    /// <summary>
    /// Creates the engine for <paramref name="arm"/>.
    /// </summary>
    /// <param name="arm">One of the engine arms; anything else throws.</param>
    /// <param name="chatClient">The harness's answering client, shared by every arm.</param>
    /// <param name="retriever">
    /// Required by <see cref="AnswerArm.Flare"/>, whose lookahead retrieves mid-generation. For
    /// <see cref="AnswerArm.FlareFixed"/> this is optional: production callers pass
    /// <see langword="null"/> and get an <see cref="UnreachableRetriever"/> stub, while tests can
    /// inject their own <see cref="UnreachableRetriever"/> instance to inspect after the call —
    /// see that type's remarks for why the instance, not the throw, is the guarantee.
    /// </param>
    public static IAnswerEngine Create(string arm, IChatClient chatClient, IRetriever? retriever)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        if (string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal))
        {
            return new ChatAnswerEngine(chatClient);
        }

        if (string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal))
        {
            return new MapReduceAnswerEngine(chatClient, NullLogger<MapReduceAnswerEngine>.Instance);
        }

        if (string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal))
        {
            return new RefineAnswerEngine(chatClient, NullLogger<RefineAnswerEngine>.Instance);
        }

        if (string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal))
        {
            return new FlareAnswerEngine(
                chatClient,
                retriever ?? new UnreachableRetriever(),
                new SelfAssessmentConfidenceScorer(chatClient),
                new FlareOptions { MaxRetrievals = 0 });
        }

        if (string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(retriever);
            return new FlareAnswerEngine(
                chatClient,
                retriever,
                new SelfAssessmentConfidenceScorer(chatClient),
                new FlareOptions());
        }

        throw new ArgumentOutOfRangeException(
            nameof(arm), arm, "Not an arm this factory builds an engine for.");
    }

    /// <summary>Reports whether <paramref name="arm"/> generates through an <see cref="IAnswerEngine"/>.</summary>
    public static bool IsEngineArm(string arm) =>
        string.Equals(arm, AnswerArm.ChatEngine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.MapReduce, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Refine, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.Flare, StringComparison.Ordinal)
        || string.Equals(arm, AnswerArm.FlareFixed, StringComparison.Ordinal);

    /// <summary>
    /// An <see cref="IRetriever"/> that records whether it was called, then throws.
    /// </summary>
    /// <remarks>
    /// <see cref="AnswerArm.FlareFixed"/>'s whole claim is that lookahead is off. The throw alone is
    /// <b>not</b> a structural guarantee of that: <c>FlareAnswerEngine.TryLookaheadRetrievalAsync</c>
    /// wraps the retriever call in a catch-all that logs and swallows every exception, including this
    /// one, and returns as if the lookahead simply found nothing — the engine keeps running, still
    /// makes its call count, and a test watching only "did it throw" or "did calls happen" would pass
    /// while lookahead had actually fired. <see cref="WasCalled"/> is set <b>before</b> the throw, so
    /// it survives that swallowing and is the guarantee callers should assert on. The throw is kept
    /// anyway: it is still correct behaviour for any caller that does not swallow it, and it costs
    /// nothing to leave in.
    /// </remarks>
    internal sealed class UnreachableRetriever : IRetriever
    {
        /// <summary>
        /// <see langword="true"/> once <see cref="RetrieveAsync"/> has been entered, regardless of
        /// what the caller does with the exception it then throws.
        /// </summary>
        public bool WasCalled { get; private set; }

        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException(
                "flarefixed retrieved mid-generation. MaxRetrievals is 0, so this is unreachable " +
                "unless FLARE's lookahead guard changed — the arm is no longer holding retrieval " +
                "fixed and its comparison against mapreduce/refine is invalid.");
        }
    }
}
