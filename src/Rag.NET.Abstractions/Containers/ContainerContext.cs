using System.Globalization;
using Rag.NET.Models;

namespace Rag.NET;

/// <summary>
/// Tracks how deep the current parse sits inside a chain of nested containers, how much of the
/// per-document container budget is left, and what the document has already cost to decompress.
/// </summary>
/// <remarks>
/// <para>
/// The state has to survive a hop through the public
/// <c>IDocumentParser.ParseAsync(Stream, DocumentMetadata, CancellationToken)</c> boundary:
/// <see cref="ContainerEntryDispatcher"/> resolves an arbitrary parser for a nested container and
/// can only reach it through that signature. <see cref="DocumentMetadata.Tags"/> is the only channel
/// that crosses it, so depth, remaining budget and bytes spent ride there under the reserved keys
/// <see cref="DepthTag"/>, <see cref="BudgetTag"/> and <see cref="BytesTag"/>.
/// </para>
/// <para>
/// All three keys are stripped from <see cref="Metadata"/> on entry, so they never reach a section, a
/// body sub-parse, or a non-container entry — and therefore never reach stored chunk metadata. The
/// caller's own dictionary is never mutated except through <see cref="ContainerBudget"/> or
/// <see cref="ContainerByteBudget"/>, and then only when the dispatcher created it.
/// </para>
/// <para>
/// The tags were named <c>__rag_email_depth</c> and <c>__rag_email_budget</c> until Phase 3.10, when
/// the archive parser needed the same accounting. They are shared rather than per-format on purpose:
/// see <see cref="ContainerContentTypes"/> for why two independent budgets would leave an
/// alternating chain bounded by neither. <see cref="BytesTag"/> was added by the phase's whole-phase
/// review, which found the byte bound left per-archive while the container bound was shared — so
/// alternating formats bought an attacker nothing but nesting the <i>same</i> format bought them a
/// fresh allowance per archive.
/// </para>
/// </remarks>
public sealed class ContainerContext
{
    /// <summary>Reserved tag carrying the nesting level of the container being parsed.</summary>
    public const string DepthTag = "__rag_container_depth";

    /// <summary>Reserved tag carrying the container budget still available.</summary>
    public const string BudgetTag = "__rag_container_budget";

    /// <summary>Reserved tag carrying the decompressed bytes the document has already cost.</summary>
    public const string BytesTag = "__rag_container_bytes";

    private ContainerContext(
        DocumentMetadata metadata,
        ContainerLimits limits,
        ContainerBudget budget,
        ContainerByteBudget bytes,
        int depth)
    {
        Metadata = metadata;
        Limits = limits;
        Budget = budget;
        Bytes = bytes;
        Depth = depth;
    }

    /// <summary>The incoming metadata with the reserved tags removed.</summary>
    public DocumentMetadata Metadata { get; }

    /// <summary>The configured nesting-depth and entry-count caps this context enforces.</summary>
    public ContainerLimits Limits { get; }

    /// <summary>The remaining nested-container allowance, shared with every context in this parse tree.</summary>
    public ContainerBudget Budget { get; }

    /// <summary>
    /// The decompressed bytes this document has cost so far, across every container in the tree. A
    /// format with no byte bound of its own — email — carries it without reading it, so a nested
    /// archive inherits what its ancestors spent.
    /// </summary>
    public ContainerByteBudget Bytes { get; }

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
        if (tags is not { Count: > 0 } || !HasReservedTag(tags))
        {
            return new ContainerContext(
                metadata,
                limits,
                new ContainerBudget(limits.MaxEntries, null),
                new ContainerByteBudget(0, null),
                0);
        }

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
        // configured cap and can only ever lower it. Bytes are carried as an amount already
        // spent rather than as an allowance left, which makes "larger is more restrictive" true
        // of them too — see ContainerByteBudget.
        int depth = ReadTag(tags, DepthTag, 0);
        int remaining = Math.Min(ReadTag(tags, BudgetTag, limits.MaxEntries), limits.MaxEntries);
        long spent = ReadLongTag(tags, BytesTag, 0);

        // Write-back is adopted only below the top level. At depth 0 the dictionary belongs to
        // the caller — it reaches stored chunk metadata — and must never be written to, even
        // when the caller happens to have set a reserved key itself.
        var sink = depth > 0 ? tags : null;
        return new ContainerContext(
            scoped, limits, new ContainerBudget(remaining, sink), new ContainerByteBudget(spent, sink), depth);
    }

    /// <summary>Derives the context a nested container parsed in-process runs under.</summary>
    public ContainerContext Descend(DocumentMetadata metadata) =>
        new(metadata, Limits, Budget, Bytes, ChildDepth);

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
    public void StampChildTags(IDictionary<string, MetadataValue> tags)
    {
        tags[DepthTag] = ChildDepth.ToString(CultureInfo.InvariantCulture);
        tags[BudgetTag] = Budget.Remaining.ToString(CultureInfo.InvariantCulture);
        tags[BytesTag] = Bytes.Spent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adopts whatever a dispatched child left behind — both the container budget and the bytes it
    /// spent — so each cap stays a total across sibling branches rather than resetting for each one.
    /// </summary>
    /// <remarks>
    /// Called after the child's enumeration whether or not the child completed it. A child that
    /// refused itself still cost the host the bytes it read before refusing, and
    /// <see cref="ContainerEntryDispatcher"/> contains that refusal, so this is the only place the
    /// parent learns of them.
    /// </remarks>
    public void AdoptChildBudget(IDictionary<string, MetadataValue> childTags)
    {
        Budget.SetRemaining(ReadTag(childTags, BudgetTag, Budget.Remaining));
        Bytes.SetSpent(ReadLongTag(childTags, BytesTag, Bytes.Spent));
    }

    internal static bool IsReservedTag(string key) =>
        string.Equals(key, DepthTag, StringComparison.Ordinal) ||
        string.Equals(key, BudgetTag, StringComparison.Ordinal) ||
        string.Equals(key, BytesTag, StringComparison.Ordinal);

    private static bool HasReservedTag(IDictionary<string, MetadataValue> tags) =>
        tags.ContainsKey(DepthTag) || tags.ContainsKey(BudgetTag) || tags.ContainsKey(BytesTag);

    private static Dictionary<string, MetadataValue> CopyWithoutReservedTags(IDictionary<string, MetadataValue> tags)
    {
        var copy = new Dictionary<string, MetadataValue>(tags.Count, StringComparer.Ordinal);
        foreach (var pair in tags)
        {
            if (!IsReservedTag(pair.Key))
                copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    // The reserved tags are framework-written as invariant strings, but the values remain
    // attacker-reachable (a connector can populate Tags from remote data), so both readers
    // parse the textual form defensively — MetadataValue.ToString is invariant for every kind —
    // rather than trusting any carried numeric kind directly.
    private static int ReadTag(IDictionary<string, MetadataValue> tags, string key, int fallback) =>
        tags.TryGetValue(key, out var raw) &&
        int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= 0
            ? value
            : fallback;

    private static long ReadLongTag(IDictionary<string, MetadataValue> tags, string key, long fallback) =>
        tags.TryGetValue(key, out var raw) &&
        long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= 0
            ? value
            : fallback;
}
