using Rag.NET.Graph;
using Rag.NET.GraphRag.LocalSearch;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.GraphRag.Tests.LocalSearch;

/// <summary>
/// Holds the context builder to Microsoft's <c>build_context</c>, section by section.
/// </summary>
/// <remarks>
/// <para>
/// These assert against the <i>specification</i> — the reading in
/// <c>docs/plans/2026-08-18-graphrag-local-search-microsoft-spec.md</c> — rather than against what
/// the builder happens to do. That distinction is the point of the file. The behaviour this
/// replaces was written from a paraphrase which had quietly dropped the entire collection step, and
/// no test caught it because every test was written from the same paraphrase.
/// </para>
/// <para>
/// So each test below names the rule it pins and where upstream states it. A test that cannot say
/// which line of the specification it defends is a test that will agree with the next mistake.
/// </para>
/// </remarks>
public sealed class LocalSearchContextBuilderTests
{
    /// <remarks>
    /// Order is not cosmetic: the local-search prompt reads these sections positionally, and the
    /// budget is spent in this order, so a section that moves also changes what fits.
    /// </remarks>
    [Fact]
    public void SectionsAppearInSpecificationOrder()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(FullInputs());

        var reports = context.Text.IndexOf("-----Reports-----", StringComparison.Ordinal);
        var entities = context.Text.IndexOf("-----Entities-----", StringComparison.Ordinal);
        var relationships = context.Text.IndexOf("-----Relationships-----", StringComparison.Ordinal);
        var sources = context.Text.IndexOf("-----Sources-----", StringComparison.Ordinal);

        Assert.True(reports >= 0, "Reports section missing.");
        Assert.True(reports < entities, "Reports must precede Entities.");
        Assert.True(entities < relationships, "Entities must precede Relationships.");
        Assert.True(relationships < sources, "Relationships must precede Sources.");
    }

    /// <remarks>
    /// The header row's tokens count against the section, and the banner is <c>-----Name-----</c>.
    /// The prompt is written against both, so this is an interface, not a formatting choice.
    /// </remarks>
    [Fact]
    public void TablesAreDelimitedWithTheHeadersTheSpecificationNames()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(FullInputs());

        Assert.Contains("-----Entities-----\nid|entity|description\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("-----Relationships-----\nid|source|target|description\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("-----Sources-----\nid|text\n", context.Text, StringComparison.Ordinal);
        Assert.Contains("-----Reports-----\nid|title|content\n", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <c>include_entity_rank</c> defaults to <see langword="false"/> upstream, and the column is
    /// the <i>only</i> place a centrality number appears in local search at all — as a display
    /// value that nothing sorts by. Pinned because this library previously blended a centrality
    /// score into similarity, and the distinction between showing a number and ranking by it is
    /// exactly what was lost.
    /// </remarks>
    [Fact]
    public void TheEntityRankColumnIsOffByDefaultAndIsOnlyEverDisplayed()
    {
        var inputs = FullInputs();

        var without = new LocalSearchContextBuilder(new LocalSearchContextOptions()).Build(inputs);
        var with = new LocalSearchContextBuilder(
            new LocalSearchContextOptions { IncludeEntityRank = true }).Build(inputs);

        Assert.DoesNotContain("number of relationships", without.Text, StringComparison.Ordinal);
        Assert.Contains("number of relationships", with.Text, StringComparison.Ordinal);

        // Same entities, same order, whether or not their degrees are on show.
        Assert.Equal(
            EntityNamesInOrder(without.Text),
            EntityNamesInOrder(with.Text));
    }

    /// <remarks>
    /// 0.15 / 0.35 / 0.5 of 12,000, from <c>LocalSearchDefaults</c> — not the <c>build_context</c>
    /// signature, which says 0.25 and 8,000 and is unreachable in a real run.
    /// </remarks>
    [Fact]
    public void TheBudgetIsSplitByTheConfigDefaultsNotTheSignatureDefaults()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(FullInputs());

        Assert.Equal(1_800, context.Reports.Budget);   // 12,000 x 0.15
        Assert.Equal(4_200, context.Entities.Budget);  // 12,000 x (1 - 0.15 - 0.5)
        Assert.Equal(6_000, context.Sources.Budget);   // 12,000 x 0.5
    }

    /// <remarks>
    /// <b>Break, not skip.</b> <c>ContextBudgetBehavior</c> skips an oversized chunk and keeps
    /// scanning; these tables stop dead. The difference matters because table rows are not
    /// independent — a table that silently omits row 3 and keeps row 4 reads as though row 3 does
    /// not exist, and the prompt cannot tell truncation from absence.
    /// </remarks>
    [Fact]
    public void AnOverlongRowEndsItsSectionRatherThanBeingSkipped()
    {
        var entities = new List<GraphEntity>
        {
            new("SHORT-A", "Thing", "tiny"),
            new("HUGE", "Thing", string.Join(' ', Enumerable.Repeat("verbose", 5_000))),
            new("SHORT-B", "Thing", "tiny"),
        };

        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(new LocalSearchInputs { SelectedEntities = entities });

        Assert.Contains("SHORT-A", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("HUGE", context.Text, StringComparison.Ordinal);

        // The one that proves break rather than skip: SHORT-B would have fitted easily.
        Assert.DoesNotContain("SHORT-B", context.Text, StringComparison.Ordinal);
        Assert.Equal(1, context.Entities.Rendered);
        Assert.True(context.Entities.Truncated);
    }

    /// <remarks>
    /// A banner and a header with no rows under them is a claim about the graph — "this section
    /// exists and is empty" — when what happened was that nothing was offered. Upstream returns an
    /// empty string; so does this.
    /// </remarks>
    [Fact]
    public void ASectionWithNothingToRenderIsAbsentRatherThanEmpty()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(new LocalSearchInputs
            {
                SelectedEntities = [new GraphEntity("SOLO", "Thing", "alone")],
            });

        Assert.Contains("-----Entities-----", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("-----Relationships-----", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("-----Reports-----", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("-----Sources-----", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Rows are newline-delimited and descriptions routinely contain newlines. Left in, a
    /// description splits into rows the prompt reads as further entities — content deciding
    /// structure, which is the shape of an injection whether or not anyone meant it as one.
    /// </remarks>
    [Fact]
    public void CellTextCannotForgeARowBoundary()
    {
        var entities = new List<GraphEntity>
        {
            new("REAL", "Thing", "harmless\n99|INJECTED|a row that was never a row"),
        };

        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(new LocalSearchInputs { SelectedEntities = entities });

        Assert.Contains("INJECTED", context.Text, StringComparison.Ordinal);
        Assert.Equal(1, context.Entities.Rendered);

        // One banner, one header, one row. The injected text is inside the row, not beside it.
        var rows = context.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);
    }

    /// <remarks>
    /// Communities are ordered by how many selected entities they contain — upstream's
    /// <c>matches</c> — before anything else. Pinned because it is the one part of upstream's
    /// community ordering this library can reproduce exactly; the secondary key is an LLM rating
    /// the reports here do not carry.
    /// </remarks>
    [Fact]
    public void CommunitiesAreOrderedByHowManySelectedEntitiesTheyHold()
    {
        var inputs = new LocalSearchInputs
        {
            SelectedEntities =
            [
                new GraphEntity("A", "Thing", "a"),
                new GraphEntity("B", "Thing", "b"),
                new GraphEntity("C", "Thing", "c"),
            ],
            Communities =
            [
                new Community(1, 0, ["A"], "one match"),
                new Community(2, 0, ["A", "B", "C"], "three matches"),
                new Community(3, 0, ["A", "B"], "two matches"),
                new Community(4, 0, ["Z"], "no matches"),
            ],
        };

        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions()).Build(inputs);

        var three = context.Text.IndexOf("three matches", StringComparison.Ordinal);
        var two = context.Text.IndexOf("two matches", StringComparison.Ordinal);
        var one = context.Text.IndexOf("one match", StringComparison.Ordinal);

        Assert.True(three < two && two < one, "Communities must be ordered by match count, descending.");
        Assert.DoesNotContain("no matches", context.Text, StringComparison.Ordinal);
        Assert.Equal(3, context.Reports.Rendered);
    }

    /// <remarks>
    /// The entity table is rendered first and cannot be evicted by relationships competing for the
    /// same section budget — so relationships get what is left, not a share.
    /// </remarks>
    [Fact]
    public void RelationshipsAreBudgetedFromWhatTheEntityTableLeaves()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions())
            .Build(FullInputs());

        Assert.Equal(4_200 - context.Entities.Tokens, context.Relationships.Budget);
        Assert.True(context.Entities.Tokens > 0, "The entity table should have cost something.");
    }

    /// <remarks>
    /// <para>
    /// Proportions summing above 1 leave the entity section a negative budget, which renders an
    /// empty entity table — a context asserting that the graph knows nothing about the query. That
    /// is the exact failure mode this reimplementation exists to remove, so it is rejected at
    /// construction rather than discovered in an answer.
    /// </para>
    /// <para>
    /// Upstream raises <c>ValueError</c> on the same condition, in <c>build_context</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.6, 0.6, "proportions summing above 1")]
    [InlineData(0.15, 0.5, null)]
    [InlineData(1.0, 0.0, null)]
    public void ProportionsThatWouldStarveTheEntitySectionAreRejected(
        double community, double textUnit, string? expectedFailure)
    {
        var options = new LocalSearchContextOptions
        {
            CommunityProportion = community,
            TextUnitProportion = textUnit,
        };

        if (expectedFailure is null)
        {
            _ = new LocalSearchContextBuilder(options);
            return;
        }

        var thrown = Assert.Throws<ArgumentException>(() => new LocalSearchContextBuilder(options));
        Assert.Contains("TextUnitProportion", thrown.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Spec section 2: history is built first and its tokens are subtracted from
    /// max_context_tokens BEFORE the three proportions divide what is left. Applying the
    /// proportions to the full total and then prepending history overruns the budget by exactly
    /// the history's size, which is the failure mode this pins.
    /// </remarks>
    [Fact]
    public void HistoryTokensComeOffTheTotalBeforeTheProportions()
    {
        var options = new LocalSearchContextOptions
        {
            MaxContextTokens = 1_000,
            CommunityProportion = 0.15,
            TextUnitProportion = 0.5,
        };

        var withoutHistory = new LocalSearchContextBuilder(options).Build(FullInputs());
        var withHistory = new LocalSearchContextBuilder(options).Build(FullInputs() with
        {
            ConversationHistory =
            [
                new ConversationTurn(ConversationRole.User, string.Join(' ', Enumerable.Repeat("spectroscopy", 100))),
            ],
        });

        Assert.True(withHistory.History.Tokens > 0, "the history section rendered nothing");
        Assert.True(
            withHistory.TokenCount <= options.MaxContextTokens,
            $"context is {withHistory.TokenCount} tokens against a {options.MaxContextTokens} budget");
        Assert.True(
            withHistory.Sources.Budget < withoutHistory.Sources.Budget,
            "the source budget did not shrink, so history was not subtracted before the split");
    }

    /// <remarks>
    /// Spec section 6: [conversation history] + [communities] + [entities, relationships,
    /// covariates] + [text units]. Order is not cosmetic — the prompt reads the sections
    /// positionally.
    /// </remarks>
    [Fact]
    public void HistoryIsTheFirstSectionInTheContext()
    {
        var context = new LocalSearchContextBuilder(new LocalSearchContextOptions()).Build(
            FullInputs() with
            {
                ConversationHistory = [new ConversationTurn(ConversationRole.User, "Who audited it?")],
            });

        var history = context.Text.IndexOf("-----Conversation History-----", StringComparison.Ordinal);
        var reports = context.Text.IndexOf("-----Reports-----", StringComparison.Ordinal);

        Assert.True(history >= 0, "no conversation history section was rendered");
        Assert.True(reports < 0 || history < reports, "history did not come first");
    }

    /// <remarks>
    /// Counter-intuitive and deliberate. See spec section 9.2: recency_bias defaults True in the
    /// docstring and is passed False by the only caller, so a long conversation contributes its
    /// BEGINNING, not its most recent exchanges. Reproduced rather than corrected, for the reason
    /// EntityOversampleScaler records: which turns reach the context changes what the model sees,
    /// so "fixing" it silently would make this a different retrieval system that resembled the
    /// specification. Set ConversationHistoryRecencyBias = true for the intuitive behaviour.
    /// </remarks>
    [Fact]
    public void TheOldestQaTurnsAreKeptBecauseUpstreamDisablesRecencyBias()
    {
        var turns = Enumerable.Range(1, 9)
            .Select(i => new ConversationTurn(ConversationRole.User, $"question {i}"))
            .ToList();

        var context = new LocalSearchContextBuilder(
                new LocalSearchContextOptions { ConversationHistoryMaxTurns = 5 })
            .Build(FullInputs() with { ConversationHistory = turns });

        Assert.Equal(5, context.History.Rendered);
        Assert.Contains("question 1", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("question 9", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecencyBiasReversesWhichTurnsSurvive()
    {
        var turns = Enumerable.Range(1, 9)
            .Select(i => new ConversationTurn(ConversationRole.User, $"question {i}"))
            .ToList();

        var context = new LocalSearchContextBuilder(
                new LocalSearchContextOptions
                {
                    ConversationHistoryMaxTurns = 5,
                    ConversationHistoryRecencyBias = true,
                })
            .Build(FullInputs() with { ConversationHistory = turns });

        Assert.Contains("question 9", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("question 1", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Six messages, three QA pairs. Spec section 9.1: only User opens a pair, so the assistant
    /// replies below are absorbed into the pair above them rather than counting against the cap.
    /// </remarks>
    [Fact]
    public void TheCapCountsQaPairsAndOnlyUserTurnsRenderByDefault()
    {
        var turns = new List<ConversationTurn>
        {
            new(ConversationRole.User, "question 1"),
            new(ConversationRole.Assistant, "answer 1"),
            new(ConversationRole.User, "question 2"),
            new(ConversationRole.Assistant, "answer 2"),
            new(ConversationRole.User, "question 3"),
            new(ConversationRole.Assistant, "answer 3"),
        };

        var context = new LocalSearchContextBuilder(
                new LocalSearchContextOptions { ConversationHistoryMaxTurns = 2 })
            .Build(FullInputs() with { ConversationHistory = turns });

        // Two pairs kept (the oldest two), and only their user halves rendered.
        Assert.Equal(2, context.History.Rendered);
        Assert.Contains("question 1", context.Text, StringComparison.Ordinal);
        Assert.Contains("question 2", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("question 3", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("answer 1", context.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistantTurnsRenderWhenUserTurnsOnlyIsOff()
    {
        var turns = new List<ConversationTurn>
        {
            new(ConversationRole.User, "question 1"),
            new(ConversationRole.Assistant, "answer 1"),
        };

        var context = new LocalSearchContextBuilder(
                new LocalSearchContextOptions { IncludeUserTurnsOnly = false })
            .Build(FullInputs() with { ConversationHistory = turns });

        Assert.Contains("user|question 1", context.Text, StringComparison.Ordinal);
        Assert.Contains("assistant|answer 1", context.Text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Deviation, deliberate — see <see cref="LocalSearchContextOptions.ConversationHistoryMaxTurns"/>.
    /// Upstream's truncation is a Python truthiness check, so <c>max_qa_turns = 0</c> is falsy there
    /// and every QA pair survives — <c>0</c> means "unlimited" upstream, an artifact of the sentinel
    /// being <c>None</c> rather than a value any real caller passes. To a C# caller of a plain
    /// <see cref="int"/> property, <c>0</c> reads as "no history", so that is what it does here:
    /// pinned so the choice is recorded rather than left to be re-discovered as a bug.
    /// </remarks>
    [Fact]
    public void ZeroMaxTurnsMeansNoHistoryNotUnlimited()
    {
        var turns = new List<ConversationTurn>
        {
            new(ConversationRole.User, "question 1"),
            new(ConversationRole.User, "question 2"),
        };

        var context = new LocalSearchContextBuilder(
                new LocalSearchContextOptions { ConversationHistoryMaxTurns = 0 })
            .Build(FullInputs() with { ConversationHistory = turns });

        Assert.Equal(0, context.History.Rendered);
        Assert.DoesNotContain("-----Conversation History-----", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("question 1", context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("question 2", context.Text, StringComparison.Ordinal);
    }

    /// <summary>A graph with every section populated.</summary>
    /// <returns>Inputs producing all four sections.</returns>
    private static LocalSearchInputs FullInputs()
    {
        var entities = new List<GraphEntity>
        {
            new("ÅNGSTRÖM", "Person", "Swedish physicist") { SourceChunkIds = ["doc1_0"] },
            new("KELVIN", "Person", "British physicist") { SourceChunkIds = ["doc1_1"] },
        };

        return new LocalSearchInputs
        {
            SelectedEntities = entities,
            Relationships = [new GraphRelationship("ÅNGSTRÖM", "KELVIN", "corresponded with")],
            Communities = [new Community(7, 0, ["ÅNGSTRÖM", "KELVIN"], "Nineteenth-century physics")],
            SourceChunks = new Dictionary<string, TextChunk>(StringComparer.Ordinal)
            {
                ["doc1_0"] = Chunk("doc1", 0, "Ångström measured the solar spectrum."),
                ["doc1_1"] = Chunk("doc1", 1, "Kelvin defined absolute temperature."),
            },
            EntityDegrees = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ÅNGSTRÖM"] = 4,
                ["KELVIN"] = 3,
            },
        };
    }

    /// <summary>Builds a source chunk.</summary>
    /// <param name="documentId">Owning document.</param>
    /// <param name="index">Position within it.</param>
    /// <param name="text">Chunk text.</param>
    /// <returns>The chunk.</returns>
    private static TextChunk Chunk(string documentId, int index, string text) => new()
    {
        Text = text,
        DocumentId = new DocumentId(documentId),
        ChunkIndex = index,
    };

    /// <summary>Reads the entity column out of a rendered context, in render order.</summary>
    /// <param name="text">The rendered context.</param>
    /// <returns>Entity names.</returns>
    private static List<string> EntityNamesInOrder(string text)
    {
        var names = new List<string>();
        var inSection = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("-----", StringComparison.Ordinal))
            {
                inSection = string.Equals(line, "-----Entities-----", StringComparison.Ordinal);
                continue;
            }

            if (inSection && line.Contains('|', StringComparison.Ordinal) &&
                !line.StartsWith("id|", StringComparison.Ordinal))
            {
                names.Add(line.Split('|')[1]);
            }
        }

        return names;
    }
}
