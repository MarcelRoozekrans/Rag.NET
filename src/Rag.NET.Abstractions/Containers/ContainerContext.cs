using System.Globalization;
using Rag.NET.Models;

namespace Rag.NET;

/// <summary>
/// Tracks how deep the current parse sits inside a chain of nested containers, and how much of the
/// per-document container budget is left.
/// </summary>
/// <remarks>
/// <para>
/// The state has to survive a hop through the public
/// <c>IDocumentParser.ParseAsync(Stream, DocumentMetadata, CancellationToken)</c> boundary:
/// <see cref="ContainerEntryDispatcher"/> resolves an arbitrary parser for a nested container and
/// can only reach it through that signature. <see cref="DocumentMetadata.Tags"/> is the only channel
/// that crosses it, so depth and remaining budget ride there under the reserved keys
/// <see cref="DepthTag"/> and <see cref="BudgetTag"/>.
/// </para>
/// <para>
/// Both keys are stripped from <see cref="Metadata"/> on entry, so they never reach a section, a
/// body sub-parse, or a non-container entry — and therefore never reach stored chunk metadata. The
/// caller's own dictionary is never mutated except through <see cref="ContainerBudget"/>, and then
/// only when the dispatcher created it.
/// </para>
/// <para>
/// The tags were named <c>__rag_email_depth</c> and <c>__rag_email_budget</c> until Phase 3.10, when
/// the archive parser needed the same accounting. They are shared rather than per-format on purpose:
/// see <see cref="ContainerContentTypes"/> for why two independent budgets would leave an
/// alternating chain bounded by neither.
/// </para>
/// </remarks>
public sealed class ContainerContext
{
    /// <summary>Reserved tag carrying the nesting level of the container being parsed.</summary>
    public const string DepthTag = "__rag_container_depth";

    /// <summary>Reserved tag carrying the container budget still available.</summary>
    public const string BudgetTag = "__rag_container_budget";

    private ContainerContext(
        DocumentMetadata metadata,
        ContainerLimits limits,
        ContainerBudget budget,
        int depth)
    {
        Metadata = metadata;
        Limits = limits;
        Budget = budget;
        Depth = depth;
    }

    /// <summary>The incoming metadata with the reserved tags removed.</summary>
    public DocumentMetadata Metadata { get; }

    public ContainerLimits Limits { get; }

    public ContainerBudget Budget { get; }

    /// <summary>Nesting level of the container being parsed; <c>0</c> for the top-level document.</summary>
    public int Depth { get; }

    /// <summary>Nesting level a container nested in the current one would occupy.</summary>
    public int ChildDepth => Depth + 1;

    /// <summary>
    /// Builds the context for a <c>ParseAsync</c> entry, reading any state left by a parent
    /// parser and returning metadata without the reserved tags.
    /// </summary>
    public static ContainerContext Create(DocumentMetadata metadata, ContainerLimits limits)
    {
        var tags = metadata.Tags;
        if (tags is not { Count: > 0 } || (!tags.ContainsKey(DepthTag) && !tags.ContainsKey(BudgetTag)))
            return new ContainerContext(metadata, limits, new ContainerBudget(limits.MaxEntries, null), 0);

        var scoped = new DocumentMetadata
        {
            DocumentId = metadata.DocumentId,
            FileName = metadata.FileName,
            ContentType = metadata.ContentType,
            Tags = CopyWithoutReservedTags(tags),
            CreatedAt = metadata.CreatedAt,
        };

        // The tags are attacker-reachable: DocumentMetadata comes from the caller, and a
        // connector can populate Tags from remote data. A larger depth is more restrictive, so
        // it is taken as read; a larger budget is less restrictive, so it is clamped to the
        // configured cap and can only ever lower it.
        int depth = ReadTag(tags, DepthTag, 0);
        int remaining = Math.Min(ReadTag(tags, BudgetTag, limits.MaxEntries), limits.MaxEntries);

        // Write-back is adopted only below the top level. At depth 0 the dictionary belongs to
        // the caller — it reaches stored chunk metadata — and must never be written to, even
        // when the caller happens to have set a reserved key itself.
        var sink = depth > 0 ? tags : null;
        return new ContainerContext(scoped, limits, new ContainerBudget(remaining, sink), depth);
    }

    /// <summary>Derives the context a nested container parsed in-process runs under.</summary>
    public ContainerContext Descend(DocumentMetadata metadata) =>
        new(metadata, Limits, Budget, ChildDepth);

    /// <summary>
    /// Reserves one nested container against both limits. Returns <see langword="false"/> after
    /// logging which limit was hit, in which case the caller skips the branch.
    /// </summary>
    /// <remarks>
    /// <see cref="ContainerLimits.MaxNestingDepth"/> of <c>0</c> skips silently: recursion was
    /// turned off deliberately, so a warning per nested container is noise rather than signal.
    /// </remarks>
    public bool TryEnterNested(string name, IContainerLog? log)
    {
        if (Limits.MaxNestingDepth == 0)
            return false;

        if (ChildDepth > Limits.MaxNestingDepth)
        {
            log?.NestingDepthExceeded(name, Limits.MaxNestingDepth);
            return false;
        }

        if (Budget.Remaining <= 0)
        {
            log?.EntryBudgetExhausted(name, Limits.MaxEntries);
            return false;
        }

        Budget.Consume();
        return true;
    }

    /// <summary>Writes the reserved tags a dispatched nested container needs to continue the count.</summary>
    public void StampChildTags(IDictionary<string, string> tags)
    {
        tags[DepthTag] = ChildDepth.ToString(CultureInfo.InvariantCulture);
        tags[BudgetTag] = Budget.Remaining.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adopts whatever budget a dispatched child left behind, so the cap stays a total across
    /// sibling branches rather than resetting for each one.
    /// </summary>
    public void AdoptChildBudget(IDictionary<string, string> childTags) =>
        Budget.SetRemaining(ReadTag(childTags, BudgetTag, Budget.Remaining));

    internal static bool IsReservedTag(string key) =>
        string.Equals(key, DepthTag, StringComparison.Ordinal) ||
        string.Equals(key, BudgetTag, StringComparison.Ordinal);

    private static Dictionary<string, string> CopyWithoutReservedTags(IDictionary<string, string> tags)
    {
        var copy = new Dictionary<string, string>(tags.Count, StringComparer.Ordinal);
        foreach (var pair in tags)
        {
            if (!IsReservedTag(pair.Key))
                copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    private static int ReadTag(IDictionary<string, string> tags, string key, int fallback) =>
        tags.TryGetValue(key, out var raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= 0
            ? value
            : fallback;
}
