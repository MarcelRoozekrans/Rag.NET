using System.Text;
using Rag.NET.Graph;
using Rag.NET.Models;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Assembles the local-search context window: community reports, then entities, relationships and
/// covariates, then source chunks — each under its own slice of a token budget.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the step that was missing.</b> The 2026-03-30 design described local search in five
/// steps and this library shipped step 4 — a PageRank blend — without step 3, the collection the
/// blend was meant to rank. What resulted re-scored whatever dense retrieval happened to return,
/// and that blend was the entire −0.02761 nDCG@10 charged to GraphRAG in Milestone 5.2: at
/// <c>PageRankWeight = 0</c> the ranking matched the control on 2,255 of 2,255 queries.
/// </para>
/// <para>
/// Faithful to <c>LocalSearchMixedContext.build_context</c>, read from source rather than
/// paraphrased — the paraphrase is what lost step 3. The reading, with the paths to re-fetch it, is
/// in <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c>. Deviations forced by
/// what this library's graph model carries are marked <b>Deviation</b> below and nowhere else.
/// </para>
/// <para>
/// Pure: no store calls, no model calls, no clock. Everything it assembles arrives in
/// <see cref="LocalSearchInputs"/>, which is what lets a test hold it to the specification a row at
/// a time.
/// </para>
/// </remarks>
public sealed class LocalSearchContextBuilder
{
    private readonly LocalSearchContextOptions _options;

    /// <summary>Creates a builder, rejecting settings that cannot produce a coherent context.</summary>
    /// <remarks>
    /// Validated here rather than at registration because the arithmetic is where the damage
    /// happens: proportions summing above 1 give the entity section a negative budget, and a
    /// negative budget renders an empty entity table — a context that claims the graph knows
    /// nothing. A silently empty section is the exact failure this whole reimplementation exists to
    /// stop, so it is an exception at construction rather than a surprise at query time.
    /// </remarks>
    /// <param name="options">Budget and ranking settings.</param>
    /// <exception cref="ArgumentException">The settings are out of range or inconsistent.</exception>
    public LocalSearchContextBuilder(LocalSearchContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var result = new LocalSearchContextOptionsValidator().Validate(options);
        if (!result.IsValid)
        {
            // Projected by index into an array: ValidationFailure is a non-readonly struct, so
            // enumerating the span by value trips EPS06 and indexing the property result directly
            // trips HLQ013 — same shape as RagBuilderExtensions.ThrowIfInvalid.
            var failures = result.Failures;
            var described = new string[failures.Length];
            for (var i = 0; i < failures.Length; i++)
            {
                described[i] = $"{failures[i].PropertyName} — {failures[i].ErrorMessage}";
            }

            throw new ArgumentException(
                "The local search context options are invalid: " + string.Join("; ", described),
                nameof(options));
        }

        _options = options;
    }

    /// <summary>Assembles a context window from already-fetched graph material.</summary>
    /// <param name="inputs">Selected entities and everything they reach.</param>
    /// <returns>The rendered context and what each section cost.</returns>
    public LocalSearchContext Build(LocalSearchInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var total = _options.MaxContextTokens;
        var communityBudget = Slice(total, _options.CommunityProportion);
        var sourceBudget = Slice(total, _options.TextUnitProportion);
        var localBudget = Slice(total, 1.0 - _options.CommunityProportion - _options.TextUnitProportion);

        var (reportText, reports) = BuildReports(inputs, communityBudget);
        var (localText, entities, relationships) = BuildLocal(inputs, localBudget);
        var (sourceText, sources) = BuildSources(inputs, sourceBudget);

        var text = Join(reportText, localText, sourceText);

        return new LocalSearchContext
        {
            Text = text,
            TokenCount = text.Length == 0 ? 0 : ContextTable.CountTokens(text),
            Reports = reports,
            Entities = entities,
            Relationships = relationships,
            Sources = sources,
        };
    }

    /// <summary>Truncates a proportion of the budget toward zero, never below zero.</summary>
    /// <param name="total">Total budget.</param>
    /// <param name="proportion">Fraction of it.</param>
    /// <returns>The section's token allowance.</returns>
    private static int Slice(int total, double proportion) =>
        Math.Max((int)(total * proportion), 0);

    /// <summary>Joins the non-empty sections in specification order.</summary>
    /// <param name="sections">Rendered sections; empty ones are dropped.</param>
    /// <returns>The context text.</returns>
    private static string Join(params string[] sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections)
        {
            if (section.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                _ = builder.Append('\n');
            }

            _ = builder.Append(section);
        }

        return builder.ToString();
    }

    /// <summary>Builds the community-report section.</summary>
    /// <remarks>
    /// <para>
    /// Ordered by how many of the selected entities belong to the community, then by community
    /// rank — the primary key exactly as upstream computes it.
    /// </para>
    /// <para>
    /// <b>Deviation.</b> Upstream's secondary key is an LLM-assigned importance rating carried on
    /// the report, and it also drops any report whose rating is missing. This library's reports
    /// carry no such rating, so the secondary key is absent and the order within an equal match
    /// count is the graph store's. Dropping unrated reports instead would drop all of them.
    /// </para>
    /// </remarks>
    /// <param name="inputs">Graph material.</param>
    /// <param name="budget">Tokens for this section.</param>
    /// <returns>Rendered section and its fill.</returns>
    private (string Text, SectionFill Fill) BuildReports(LocalSearchInputs inputs, int budget)
    {
        if (inputs.SelectedEntities.Count == 0 || inputs.Communities.Count == 0)
        {
            return (string.Empty, new SectionFill(0, 0, 0, budget));
        }

        var ordered = OrderCommunitiesByMatches(inputs);
        var table = new ContextTable("Reports", ["id", "title", "content"], _options.ColumnDelimiter, budget);

        for (var i = 0; i < ordered.Count; i++)
        {
            var community = ordered[i];
            var title = $"Community {community.Id} (level {community.Level})";
            if (!table.TryAdd(
                community.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                title,
                Clean(community.ReportSummary)))
            {
                break;
            }
        }

        return (table.Render(), new SectionFill(table.Rendered, ordered.Count, table.Tokens, budget));
    }

    /// <summary>Orders the communities the selected entities belong to, most-matched first.</summary>
    /// <param name="inputs">Graph material.</param>
    /// <returns>Communities with at least one selected member and a report, in render order.</returns>
    private static List<Community> OrderCommunitiesByMatches(LocalSearchInputs inputs)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < inputs.SelectedEntities.Count; i++)
        {
            _ = selected.Add(inputs.SelectedEntities[i].Name);
        }

        var matched = new List<(Community Community, int Matches)>();
        foreach (var community in inputs.Communities)
        {
            if (string.IsNullOrWhiteSpace(community.ReportSummary))
            {
                continue;
            }

            var matches = 0;
            for (var i = 0; i < community.MemberEntities.Count; i++)
            {
                if (selected.Contains(community.MemberEntities[i]))
                {
                    matches++;
                }
            }

            if (matches > 0)
            {
                matched.Add((community, matches));
            }
        }

        return matched.OrderByDescending(m => m.Matches).Select(m => m.Community).ToList();
    }

    /// <summary>Builds the entity and relationship section.</summary>
    /// <remarks>
    /// <para>
    /// The entity table is rendered first and cannot be evicted — its tokens count against the
    /// section's budget, but no later step removes rows from it. Then relationships are admitted
    /// against the remainder.
    /// </para>
    /// <para>
    /// <b>Deviation, deliberate.</b> Upstream rebuilds the whole relationship table once per
    /// selected entity, from a growing prefix of the selection, keeping the last version that fit —
    /// quadratic, and the only observable consequence is which relationships appear, since the
    /// ranking is over the full selection either way. Here the relationship set is computed once
    /// over the whole selection and admitted row by row until the budget stops it, which selects
    /// the same prefix of the same ranking. The costed loop is reproduced only if
    /// <see cref="LocalSearchContextOptions"/> ever grows a covariate section that changes the sum
    /// per iteration.
    /// </para>
    /// </remarks>
    /// <param name="inputs">Graph material.</param>
    /// <param name="budget">Tokens for this section.</param>
    /// <returns>Rendered section, entity fill, relationship fill.</returns>
    private (string Text, SectionFill Entities, SectionFill Relationships) BuildLocal(
        LocalSearchInputs inputs, int budget)
    {
        var entityTable = BuildEntityTable(inputs, budget);
        var entityText = entityTable.Render();
        var entityFill = new SectionFill(
            entityTable.Rendered, inputs.SelectedEntities.Count, entityTable.Tokens, budget);

        var selected = RelationshipSelection.Select(
            inputs.SelectedEntities, inputs.Relationships, inputs.EntityDegrees, _options.TopKRelationships);

        var remaining = Math.Max(budget - entityTable.Tokens, 0);
        var relationshipTable = BuildRelationshipTable(selected, remaining);
        var relationshipText = relationshipTable.Render();
        var relationshipFill = new SectionFill(
            relationshipTable.Rendered, selected.Count, relationshipTable.Tokens, remaining);

        return (Join(entityText, relationshipText), entityFill, relationshipFill);
    }

    /// <summary>Renders the entity table, in selection order.</summary>
    /// <param name="inputs">Graph material.</param>
    /// <param name="budget">Tokens for the local section.</param>
    /// <returns>The table.</returns>
    private ContextTable BuildEntityTable(LocalSearchInputs inputs, int budget)
    {
        var header = _options.IncludeEntityRank
            ? new[] { "id", "entity", "description", "number of relationships" }
            : ["id", "entity", "description"];

        var table = new ContextTable("Entities", header, _options.ColumnDelimiter, budget);

        for (var i = 0; i < inputs.SelectedEntities.Count; i++)
        {
            var entity = inputs.SelectedEntities[i];
            var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var degree = inputs.EntityDegrees.TryGetValue(entity.Name, out var d) ? d : 0;

            var added = _options.IncludeEntityRank
                ? table.TryAdd(id, entity.Name, Clean(entity.Description), degree.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : table.TryAdd(id, entity.Name, Clean(entity.Description));

            if (!added)
            {
                break;
            }
        }

        return table;
    }

    /// <summary>Renders the relationship table, in selection order.</summary>
    /// <param name="selected">Relationships, already ranked and capped.</param>
    /// <param name="budget">Tokens left after the entity table.</param>
    /// <returns>The table.</returns>
    private ContextTable BuildRelationshipTable(List<GraphRelationship> selected, int budget)
    {
        var header = _options.IncludeRelationshipWeight
            ? new[] { "id", "source", "target", "description", "weight" }
            : ["id", "source", "target", "description"];

        var table = new ContextTable("Relationships", header, _options.ColumnDelimiter, budget);

        for (var i = 0; i < selected.Count; i++)
        {
            var rel = selected[i];
            var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var added = _options.IncludeRelationshipWeight
                ? table.TryAdd(id, rel.SourceEntity, rel.TargetEntity, Clean(rel.Description), rel.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : table.TryAdd(id, rel.SourceEntity, rel.TargetEntity, Clean(rel.Description));

            if (!added)
            {
                break;
            }
        }

        return table;
    }

    /// <summary>Builds the source-chunk section.</summary>
    /// <param name="inputs">Graph material.</param>
    /// <param name="budget">Tokens for this section.</param>
    /// <returns>Rendered section and its fill.</returns>
    private (string Text, SectionFill Fill) BuildSources(LocalSearchInputs inputs, int budget)
    {
        var ordered = SourceChunkSelection.Select(inputs);
        if (ordered.Count == 0)
        {
            return (string.Empty, new SectionFill(0, 0, 0, budget));
        }

        var table = new ContextTable("Sources", ["id", "text"], _options.ColumnDelimiter, budget);

        for (var i = 0; i < ordered.Count; i++)
        {
            var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!table.TryAdd(id, Clean(ordered[i].Text)))
            {
                break;
            }
        }

        return (table.Render(), new SectionFill(table.Rendered, ordered.Count, table.Tokens, budget));
    }

    /// <summary>Flattens newlines so one cell cannot forge a row boundary.</summary>
    /// <remarks>
    /// The tables are newline-delimited, and entity descriptions and chunk text both routinely
    /// contain newlines. Left in, a description would split into rows the prompt reads as separate
    /// entities — content deciding structure, which is the shape of an injection whether or not
    /// anyone meant it as one. Upstream does not do this; upstream also writes CSV through pandas
    /// rather than joining strings.
    /// </remarks>
    /// <param name="value">Cell text.</param>
    /// <returns>The text with newlines replaced by spaces.</returns>
    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace('\r', ' ').Replace('\n', ' ');
}
