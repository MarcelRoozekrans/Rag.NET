using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins every (dataset, protocol) pair the harness declares inapplicable, as a literal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Applicability is a skip, and a skip reads like a pass.</b> Seven theories now consult
/// <see cref="BeirDatasetDescriptor.Supports"/> before they consult the machine, and both registries
/// require a cell where a protocol applies and refuse one where it does not. That is the right
/// shape, and it is also a mechanism for making an expensive measurement disappear quietly: one
/// protocol removed from one descriptor turns a run into a skip, and nothing else in the suite has
/// an opinion about it.
/// </para>
/// <para>
/// So the set is restated here as a literal rather than computed. The expected values below were
/// written in the plan before any of the code they constrain existed, and copied in unchanged —
/// which is the whole point, because a set computed from the descriptors would agree with whatever
/// the descriptors ever say. If this fails, the descriptors are wrong; the expected set is not the
/// thing to edit.
/// </para>
/// </remarks>
public sealed class ProtocolApplicabilityTests
{
    [Fact]
    public void TheInapplicablePairsAreExactlyThese_SoApplicabilityCannotHideAFailingRun()
    {
        // Restated as a literal rather than computed from the descriptors, which is the whole point:
        // computing it from the source it is meant to constrain would agree with any value that
        // source ever takes.
        var expected = ExpectedInapplicablePairs();
        expected.UnionWith(ExpectedInapplicableBeirPairs());

        var actual = new HashSet<string>(
            from d in BeirDatasetDescriptor.All
            from p in Enum.GetValues<BeirProtocol>()
            where !d.Supports(p)
            select $"{d.Name}/{p}",
            StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>Every (dataset, protocol) pair that is inapplicable by design.</summary>
    /// <remarks>
    /// Hoisted out of the test only because the list outgrew the method-length analyser; it is the
    /// assertion's whole substance. Naming every pair is what stops an applicability change from
    /// silently gating off a measured cell, which reads from a test summary exactly like a pass.
    /// </remarks>
    private static HashSet<string> ExpectedInapplicablePairs() =>
        new(StringComparer.Ordinal)
            {
                "multihop-rag/Parity",
                "multihop-rag/HybridBm25",
                "multihop-rag/Hyde",
                "multihop-rag/Reranked",
                "multihop-rag/Comparison",
                "multihop-rag/SemanticKernel",
                "multihop-rag/LangChain",
                "multihop-rag/LlamaIndex",
                "multihop-rag/Haystack",

                // Phase 6.2.1: SemanticChunking applies to the four BEIR datasets and not to
                // multihop-rag, which carries only the graph protocols. A new protocol necessarily adds
                // a pair here for every descriptor that does not declare it — that is the set growing
                // because the enum grew, not applicability being quietly narrowed, which is what the
                // remarks above warn against.
                "multihop-rag/SemanticChunking",

                // Phase 6.2.1: RealHyde and RealReranked measure HyDE and cross-encoder reranking over
                // Rag.NET's own chunking rather than over parity's one-chunk-per-document units. They
                // apply to the same four BEIR datasets SemanticChunking does, and not to multihop-rag,
                // which carries only the graph protocols. Two more pairs for the same reason as the line
                // above: the enum grew.
                "multihop-rag/RealHyde",
                "multihop-rag/RealReranked",
                "multihop-rag/RealHybridBm25",
                "multihop-rag/RealLateChunking",
                "multihop-rag/RealSplade",

            };

    /// <summary>The pairs inapplicable because the protocol belongs to a BEIR dataset, not to all.</summary>
    /// <remarks>
    /// Split from <see cref="ExpectedInapplicablePairs"/> only because the combined list outgrew the
    /// method-length analyser. The seam is the natural one: everything above is inapplicable because
    /// it belongs to the four BEIR datasets and multihop-rag carries only the graph protocols;
    /// everything here is inapplicable in the other direction.
    /// </remarks>
    private static IReadOnlyList<string> ExpectedInapplicableBeirPairs() =>
        [
            "scifact/GraphRag",
                "fiqa/GraphRag",
                "arguana/GraphRag",
                "trec-covid/GraphRag",

                // Phase 5.2.1 (#232): the graph path's depth-matched dense control is inapplicable
                // exactly where the graph path is — a control with no graph run to control for would
                // be a dense run at an arbitrary depth, and it would owe a cell and a pin for nothing.
                "scifact/GraphRagDepthControl",
                "fiqa/GraphRagDepthControl",
                "arguana/GraphRagDepthControl",
                "trec-covid/GraphRagDepthControl",

                // Phase 6.2.1: RealTagFiltered is SciFact's and nowhere else's, which makes it the
                // FIRST protocol inapplicable to BEIR datasets rather than to multihop-rag. It names a
                // store composition -- SciFact retrieved out of a SciFact+FiQA store -- instead of a
                // technique applied to a corpus, so each of the others would need a different pairing,
                // control and target. Those are runs that do not exist, not runs nobody scheduled.
                "fiqa/RealTagFiltered",
                "arguana/RealTagFiltered",
                "trec-covid/RealTagFiltered",
                "multihop-rag/RealTagFiltered",

            // Phase 6.2.1: RealSelfQuery is SciFact's for the same reason as RealTagFiltered --
            // it names the same two-corpus store, and asks a model to write the filter that cell
            // applies by hand. The other three would each need a different pairing.
            "fiqa/RealSelfQuery",
            "arguana/RealSelfQuery",
            "trec-covid/RealSelfQuery",
            "multihop-rag/RealSelfQuery",
        ];
}
