using Rag.NET.Graph;
using Rag.NET.Models;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Orders the source chunks the selected entities were extracted from, by upstream's
/// <c>_build_text_unit_context</c> rule.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is <c>(entity_order, −relationship_count)</c>, and the leading term is the one to
/// notice: <b>the first selected entity's chunks all precede the second's</b>, however each chunk
/// scored against the query. Half the context budget is spent by this ordering, so getting it
/// wrong is not a detail — a similarity-ordered version of this section would be a different
/// retrieval system that happened to read from a graph.
/// </para>
/// <para>
/// Source:
/// <c>packages/graphrag/graphrag/query/structured_search/local_search/mixed_context.py::_build_text_unit_context</c>
/// and <c>query/context_builder/source_context.py::count_relationships</c>.
/// </para>
/// </remarks>
internal static class SourceChunkSelection
{
    /// <summary>Selects and orders source chunks for the sources section.</summary>
    /// <param name="inputs">Graph material.</param>
    /// <returns>Chunks in render order, each appearing once.</returns>
    internal static List<TextChunk> Select(LocalSearchInputs inputs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(TextChunk Chunk, int EntityOrder, int Relationships)>();

        for (var order = 0; order < inputs.SelectedEntities.Count; order++)
        {
            var entity = inputs.SelectedEntities[order];
            var entityRelationships = RelationshipsOf(entity, inputs.Relationships);

            foreach (var chunkId in entity.SourceChunkIds)
            {
                // First entity to claim a chunk keeps it, which is what makes the primary sort key
                // meaningful: a chunk shared by entities 0 and 5 belongs to 0's block, not both.
                if (!seen.Add(chunkId) || !inputs.SourceChunks.TryGetValue(chunkId, out var chunk))
                {
                    continue;
                }

                candidates.Add((chunk, order, CountRelationships(entityRelationships, chunkId)));
            }
        }

        return candidates
            .OrderBy(c => c.EntityOrder)
            .ThenByDescending(c => c.Relationships)
            .Select(c => c.Chunk)
            .ToList();
    }

    /// <summary>Collects the relationships with this entity at either end.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="relationships">Every candidate relationship.</param>
    /// <returns>Those touching the entity.</returns>
    private static List<GraphRelationship> RelationshipsOf(
        GraphEntity entity, IReadOnlyList<GraphRelationship> relationships)
    {
        var touching = new List<GraphRelationship>();
        for (var i = 0; i < relationships.Count; i++)
        {
            var rel = relationships[i];
            if (string.Equals(rel.SourceEntity, entity.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel.TargetEntity, entity.Name, StringComparison.OrdinalIgnoreCase))
            {
                touching.Add(rel);
            }
        }

        return touching;
    }

    /// <summary>
    /// Counts how many of the entity's own relationships were extracted from this chunk.
    /// </summary>
    /// <remarks>
    /// The secondary sort key, and it degrades honestly: a graph whose relationships carry no chunk
    /// provenance returns 0 for every chunk, leaving the entity order intact and the order within
    /// one entity's block as the store gave it. That is the state of every graph written before
    /// <see cref="GraphRelationship.SourceChunkIds"/> existed, and it costs ordering within a block
    /// rather than correctness of the block.
    /// </remarks>
    /// <param name="entityRelationships">Relationships touching the entity.</param>
    /// <param name="chunkId">The chunk in question.</param>
    /// <returns>How many of them name the chunk.</returns>
    private static int CountRelationships(
        List<GraphRelationship> entityRelationships, string chunkId)
    {
        var count = 0;
        for (var i = 0; i < entityRelationships.Count; i++)
        {
            var ids = entityRelationships[i].SourceChunkIds;
            for (var j = 0; j < ids.Count; j++)
            {
                if (string.Equals(ids[j], chunkId, StringComparison.Ordinal))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }
}
