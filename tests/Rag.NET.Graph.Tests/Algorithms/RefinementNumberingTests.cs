using System.Globalization;
using Rag.NET.Graph.Algorithms;
using Xunit;

namespace Rag.NET.Graph.Tests.Algorithms;

/// <summary>
/// The refinement phase must produce the same partition however its sub-communities are numbered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two index spaces met in one array and nothing said so.</b> <c>RefineSingleNode</c> read
/// <c>assignment[comm]</c> to decide whether a candidate sub-community lay inside the node's
/// community, where <c>comm</c> is a sub-community id and <c>assignment</c> is indexed by node
/// index everywhere else in the file. It gave the right answer only because
/// <c>RefinementPhase</c> labels each starting sub-community with its own node's index, so the two
/// spaces coincided numerically. Nothing enforced that and nothing tested it.
/// </para>
/// <para>
/// <b>Why this is a test and not a comment.</b> Renumbering sub-communities is an ordinary thing to
/// want to do — densely from zero is the obvious way to key an array instead of a dictionary — and
/// under the old code it did not throw. It read a real element of <c>assignment</c> belonging to an
/// unrelated node and quietly answered the wrong question, so the partition changed and every clique
/// test still passed. These two labellings break the coincidence in the two ways it can break: past
/// the end of <c>assignment</c>, where the old code's bounds check dropped the constraint entirely
/// and let sub-communities merge across community lines; and inside it but permuted, where it kept
/// the constraint and applied it to the wrong node.
/// </para>
/// </remarks>
public class RefinementNumberingTests
{
    private const int NodeCount = 6;

    /// <summary>
    /// Two triangles bridged by an edge ten times heavier than any inside them.
    /// </summary>
    /// <remarks>
    /// The bridge is heavy so that merging across the two communities is by far the most attractive
    /// move available, and only the community constraint stops it. On an evenly weighted graph the
    /// constraint can be dropped altogether and the greedy still declines the cross-community merge,
    /// which would leave these tests passing over the defect they exist to catch.
    /// </remarks>
    private static readonly (int From, int To, double Weight)[] Edges =
    [
        (0, 1, 1.0), (0, 2, 1.0), (1, 2, 1.0),
        (3, 4, 1.0), (3, 5, 1.0), (4, 5, 1.0),
        (2, 3, 10.0),
    ];

    /// <summary>The partition the local-moving phase hands the refinement: one triangle each.</summary>
    private static readonly int[] Assignment = [0, 0, 0, 3, 3, 3];

    [Fact]
    public void Refine_SubCommunitiesNumberedPastTheNodeCount_ProducesTheSamePartition()
    {
        var offset = new int[NodeCount];
        for (int i = 0; i < NodeCount; i++)
        {
            offset[i] = i + 1000;
        }

        AssertPartitionMatchesNodeIndexNumbering(offset);
    }

    [Fact]
    public void Refine_SubCommunitiesNumberedDenselyFromZero_ProducesTheSamePartition()
    {
        // Dense and zero-based, as any array-keyed numbering would be, but not each node's own index.
        var dense = new int[NodeCount];
        for (int i = 0; i < NodeCount; i++)
        {
            dense[i] = NodeCount - 1 - i;
        }

        AssertPartitionMatchesNodeIndexNumbering(dense);
    }

    private static void AssertPartitionMatchesNodeIndexNumbering(int[] numbering)
    {
        var nodeIndexNumbering = new int[NodeCount];
        for (int i = 0; i < NodeCount; i++)
        {
            nodeIndexNumbering[i] = i;
        }

        string expected = Describe(Refine(nodeIndexNumbering));
        string actual = Describe(Refine(numbering));

        Assert.Equal(expected, actual);
    }

    private static int[] Refine(int[] singletonCommunities)
    {
        var neighbors = new List<int>[NodeCount];
        var weights = new List<double>[NodeCount];
        for (int i = 0; i < NodeCount; i++)
        {
            neighbors[i] = [];
            weights[i] = [];
        }

        double totalWeight = 0.0;
        foreach (var (from, to, weight) in Edges)
        {
            neighbors[from].Add(to);
            weights[from].Add(weight);
            neighbors[to].Add(from);
            weights[to].Add(weight);
            totalWeight += weight;
        }

        return Leiden.Refine(
            neighbors, weights, NodeCount, Assignment, singletonCommunities,
            totalWeight, new LeidenOptions(), new Random(42));
    }

    /// <summary>
    /// Renders a partition by its groups of node indices, so two runs are comparable however their
    /// communities are labelled — the labels are exactly what these tests are allowed to differ in.
    /// </summary>
    private static string Describe(int[] partition)
    {
        var groups = new Dictionary<int, List<string>>();
        for (int i = 0; i < partition.Length; i++)
        {
            if (!groups.TryGetValue(partition[i], out var members))
            {
                members = [];
                groups[partition[i]] = members;
            }

            members.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        var rendered = new List<string>(groups.Count);
        foreach (var (_, members) in groups)
        {
            rendered.Add($"{{{string.Join(",", members)}}}");
        }

        rendered.Sort(StringComparer.Ordinal);
        return string.Join(" ", rendered);
    }
}
