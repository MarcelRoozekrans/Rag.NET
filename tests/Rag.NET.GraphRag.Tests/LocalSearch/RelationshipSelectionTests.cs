using Rag.NET.Graph;
using Rag.NET.GraphRag.LocalSearch;
using Xunit;

namespace Rag.NET.GraphRag.Tests.LocalSearch;

/// <summary>
/// Pins upstream's <c>_filter_relationships</c>, whose plausible summary — "the top ten
/// relationships" — is wrong in all three of its parts.
/// </summary>
/// <remarks>
/// This file exists because the first draft of the design document said "capped at
/// <c>top_k_relationships</c>", which is what anyone would write after reading the parameter name.
/// The actual rule caps only the out-of-network list, at <c>top_k × selected_count</c>, and leaves
/// in-network relationships untouched. Each test below is one of the three parts.
/// </remarks>
public sealed class RelationshipSelectionTests
{
    /// <remarks>
    /// In-network — both endpoints selected — is never truncated, at any selection size. The cap
    /// does not apply to it, and a reader who assumes otherwise loses exactly the relationships the
    /// query is most about.
    /// </remarks>
    [Fact]
    public void InNetworkRelationshipsAreNeverTruncated()
    {
        var entities = Entities(20);
        var relationships = new List<GraphRelationship>();

        // Every pair among the selected entities: 190 edges, all in-network.
        for (var i = 0; i < 20; i++)
        {
            for (var j = i + 1; j < 20; j++)
            {
                relationships.Add(new GraphRelationship($"E{i}", $"E{j}", "linked"));
            }
        }

        var selected = RelationshipSelection.Select(entities, relationships, Degrees(entities), 1);

        // topKRelationships = 1 would cap an out-of-network list at 20. It caps this at nothing.
        Assert.Equal(190, selected.Count);
    }

    /// <remarks>
    /// The cap is <c>top_k × selected_count</c>, not <c>top_k</c>. At the shipped defaults that is
    /// 200 relationships, and reading it as 10 would discard 95% of the section.
    /// </remarks>
    [Fact]
    public void TheOutOfNetworkCapScalesWithTheSelectionSize()
    {
        var entities = Entities(3);
        var relationships = new List<GraphRelationship>();
        for (var i = 0; i < 100; i++)
        {
            relationships.Add(new GraphRelationship("E0", $"OUTSIDE{i}", "linked"));
        }

        var selected = RelationshipSelection.Select(entities, relationships, Degrees(entities), 10);

        Assert.Equal(30, selected.Count);
    }

    /// <remarks>
    /// <para>
    /// The out-of-network sort leads on <c>links</c> — how many distinct selected entities reach
    /// that outside entity — and only then on combined degree. So a modest entity that three seeds
    /// all point at outranks a hub reached from one.
    /// </para>
    /// <para>
    /// This is the substantive claim of the whole ranking: a shared neighbour is evidence about the
    /// query, a high-degree one is evidence about the corpus.
    /// </para>
    /// </remarks>
    [Fact]
    public void SharedOutsideEntitiesOutrankBetterConnectedLonelyOnes()
    {
        var entities = Entities(3);
        var relationships = new List<GraphRelationship>
        {
            new("E0", "HUB", "linked"),
            new("E0", "SHARED", "linked"),
            new("E1", "SHARED", "linked"),
            new("E2", "SHARED", "linked"),
        };

        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["E0"] = 2, ["E1"] = 1, ["E2"] = 1,
            ["HUB"] = 5_000,
            ["SHARED"] = 3,
        };

        var selected = RelationshipSelection.Select(entities, relationships, degrees, 10);

        Assert.Equal("SHARED", selected[0].TargetEntity);
        Assert.Equal("HUB", selected[^1].TargetEntity);
    }

    /// <remarks>
    /// In-network first, then out-of-network — the concatenation order, not a re-sort of the union.
    /// A low-degree in-network edge still precedes a high-degree out-of-network one.
    /// </remarks>
    [Fact]
    public void InNetworkRelationshipsPrecedeOutOfNetworkOnesRegardlessOfRank()
    {
        var entities = Entities(2);
        var relationships = new List<GraphRelationship>
        {
            new("E0", "FAMOUS", "linked"),
            new("E1", "ALSO-FAMOUS", "linked"),
            new("E0", "E1", "linked"),
        };

        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["E0"] = 1, ["E1"] = 1,
            ["FAMOUS"] = 900, ["ALSO-FAMOUS"] = 900,
        };

        var selected = RelationshipSelection.Select(entities, relationships, degrees, 10);

        Assert.Equal("E1", selected[0].TargetEntity);
    }

    /// <remarks>
    /// Ties are common — every edge between two degree-1 entities ranks equal — and upstream's sort
    /// is stable, so ties keep input order. An unstable sort would make the table depend on the
    /// input size rather than on the graph, and no test could pin the result.
    /// </remarks>
    [Fact]
    public void TiesKeepTheirInputOrder()
    {
        var entities = Entities(2);
        var relationships = new List<GraphRelationship>();
        for (var i = 0; i < 40; i++)
        {
            relationships.Add(new GraphRelationship("E0", $"TIED{i:D2}", "linked"));
        }

        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = RelationshipSelection.Select(entities, relationships, degrees, 10);

        for (var i = 0; i < selected.Count; i++)
        {
            Assert.Equal($"TIED{i:D2}", selected[i].TargetEntity);
        }
    }

    /// <remarks>
    /// A relationship's rank is the sum of its two endpoints' whole-graph degrees — upstream's
    /// <c>combined_degree</c>, confirmed at <c>indexer_adapters.py::read_indexer_relationships</c>.
    /// Not either endpoint alone, and not a degree counted over the selection.
    /// </remarks>
    [Fact]
    public void RankIsTheSumOfBothEndpointDegrees()
    {
        var entities = Entities(1);
        var relationships = new List<GraphRelationship>
        {
            new("E0", "LOW", "linked"),
            new("E0", "HIGH", "linked"),
        };

        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["E0"] = 10,
            ["LOW"] = 1,
            ["HIGH"] = 2,
        };

        var selected = RelationshipSelection.Select(entities, relationships, degrees, 10);

        // Both have links = 1, so the tie-break is 10+2 against 10+1.
        Assert.Equal("HIGH", selected[0].TargetEntity);
    }

    /// <summary>Builds <paramref name="count"/> entities named <c>E0..En</c>.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The entities.</returns>
    private static List<GraphEntity> Entities(int count)
    {
        var entities = new List<GraphEntity>(count);
        for (var i = 0; i < count; i++)
        {
            entities.Add(new GraphEntity($"E{i}", "Thing", "described"));
        }

        return entities;
    }

    /// <summary>Gives every entity degree 1, so ranking is flat and other keys show through.</summary>
    /// <param name="entities">The entities.</param>
    /// <returns>Degrees by name.</returns>
    private static Dictionary<string, int> Degrees(List<GraphEntity> entities)
    {
        var degrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < entities.Count; i++)
        {
            degrees[entities[i].Name] = 1;
        }

        return degrees;
    }
}
