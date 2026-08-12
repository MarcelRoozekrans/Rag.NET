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
        var expected = new HashSet<string>(StringComparer.Ordinal)
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
            "scifact/GraphRag",
            "fiqa/GraphRag",
            "arguana/GraphRag",
            "trec-covid/GraphRag",
        };

        var actual = new HashSet<string>(
            from d in BeirDatasetDescriptor.All
            from p in Enum.GetValues<BeirProtocol>()
            where !d.Supports(p)
            select $"{d.Name}/{p}",
            StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }
}
