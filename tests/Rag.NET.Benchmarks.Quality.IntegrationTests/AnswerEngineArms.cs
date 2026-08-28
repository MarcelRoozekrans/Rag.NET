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
    /// Required by <see cref="AnswerArm.Flare"/> only, whose lookahead retrieves mid-generation.
    /// <see cref="AnswerArm.FlareFixed"/> is given an <see cref="UnreachableRetriever"/> instead:
    /// at <c>MaxRetrievals = 0</c> the retriever cannot be reached, so a stub that throws turns
    /// "lookahead is off" from an observation into a structural guarantee.
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
                new UnreachableRetriever(),
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
    /// An <see cref="IRetriever"/> that throws if it is ever called.
    /// </summary>
    /// <remarks>
    /// <see cref="AnswerArm.FlareFixed"/>'s whole claim is that lookahead is off. A counter reading
    /// zero and a code path that cannot execute are different guarantees, and this is the second
    /// one: if a future change ever reaches the retriever, the arm fails loudly instead of quietly
    /// retrieving and reporting as a fixed-context arm.
    /// </remarks>
    internal sealed class UnreachableRetriever : IRetriever
    {
        public Task<Result<IReadOnlyList<SearchResult>, RagError>> RetrieveAsync(
            string query,
            RetrievalOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "flarefixed retrieved mid-generation. MaxRetrievals is 0, so this is unreachable " +
                "unless FLARE's lookahead guard changed — the arm is no longer holding retrieval " +
                "fixed and its comparison against mapreduce/refine is invalid.");
    }
}
