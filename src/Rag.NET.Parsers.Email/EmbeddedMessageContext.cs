using System.Globalization;
using Microsoft.Extensions.Logging;
using Rag.NET.Models;

namespace Rag.NET.Parsers.Email;

/// <summary>
/// Tracks how deep the current parse sits inside a chain of embedded messages, and how much
/// of the per-document embedded-message budget is left.
/// </summary>
/// <remarks>
/// <para>
/// The state has to survive a hop through the public
/// <c>IDocumentParser.ParseAsync(Stream, DocumentMetadata, CancellationToken)</c> boundary:
/// <see cref="EmailAttachmentDispatcher"/> resolves an arbitrary parser for a
/// <c>message/rfc822</c> or <c>application/vnd.ms-outlook</c> attachment and can only reach it
/// through that signature. <see cref="DocumentMetadata.Tags"/> is the only channel that
/// crosses it, so depth and remaining budget ride there under the reserved keys
/// <see cref="DepthTag"/> and <see cref="BudgetTag"/>.
/// </para>
/// <para>
/// Both keys are stripped from <see cref="Metadata"/> on entry, so they never reach a section,
/// a body sub-parse, or a non-message attachment — and therefore never reach stored chunk
/// metadata. The caller's own dictionary is never mutated except through
/// <see cref="EmbeddedMessageBudget"/>, and then only when the dispatcher created it.
/// </para>
/// </remarks>
internal sealed class EmbeddedMessageContext
{
    /// <summary>Reserved tag carrying the nesting level of the message being parsed.</summary>
    internal const string DepthTag = "__rag_email_depth";

    /// <summary>Reserved tag carrying the embedded-message budget still available.</summary>
    internal const string BudgetTag = "__rag_email_budget";

    private EmbeddedMessageContext(
        DocumentMetadata metadata,
        EmailParserOptions options,
        EmbeddedMessageBudget budget,
        int depth)
    {
        Metadata = metadata;
        Options = options;
        Budget = budget;
        Depth = depth;
    }

    /// <summary>The incoming metadata with the reserved tags removed.</summary>
    public DocumentMetadata Metadata { get; }

    public EmailParserOptions Options { get; }

    public EmbeddedMessageBudget Budget { get; }

    /// <summary>Nesting level of the message being parsed; <c>0</c> for the top-level document.</summary>
    public int Depth { get; }

    /// <summary>Nesting level a message embedded in the current one would occupy.</summary>
    public int ChildDepth => Depth + 1;

    /// <summary>
    /// Builds the context for a <c>ParseAsync</c> entry, reading any state left by a parent
    /// parser and returning metadata without the reserved tags.
    /// </summary>
    public static EmbeddedMessageContext Create(DocumentMetadata metadata, EmailParserOptions options)
    {
        var tags = metadata.Tags;
        if (tags is not { Count: > 0 } || (!tags.ContainsKey(DepthTag) && !tags.ContainsKey(BudgetTag)))
            return new EmbeddedMessageContext(metadata, options, new EmbeddedMessageBudget(options.MaxEmbeddedMessages, null), 0);

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
        int remaining = Math.Min(ReadTag(tags, BudgetTag, options.MaxEmbeddedMessages), options.MaxEmbeddedMessages);

        // Write-back is adopted only below the top level. At depth 0 the dictionary belongs to
        // the caller — it reaches stored chunk metadata — and must never be written to, even
        // when the caller happens to have set a reserved key itself.
        var sink = depth > 0 ? tags : null;
        return new EmbeddedMessageContext(scoped, options, new EmbeddedMessageBudget(remaining, sink), depth);
    }

    /// <summary>Derives the context an embedded message parsed in-process runs under.</summary>
    public EmbeddedMessageContext Descend(DocumentMetadata metadata) =>
        new(metadata, Options, Budget, ChildDepth);

    /// <summary>
    /// Reserves one embedded message against both limits. Returns <see langword="false"/>
    /// after logging which limit was hit, in which case the caller skips the branch.
    /// </summary>
    /// <remarks>
    /// <c>MaxEmbeddedDepth = 0</c> skips silently: recursion was turned off deliberately, so a
    /// warning per embedded message is noise rather than signal.
    /// </remarks>
    public bool TryEnterEmbedded(string name, ILogger? logger)
    {
        if (Options.MaxEmbeddedDepth == 0)
            return false;

        if (ChildDepth > Options.MaxEmbeddedDepth)
        {
            if (logger is not null)
                EmailParserLog.EmbeddedMessageDepthLimit(logger, name, Options.MaxEmbeddedDepth);
            return false;
        }

        if (Budget.Remaining <= 0)
        {
            if (logger is not null)
                EmailParserLog.EmbeddedMessageCountLimit(logger, name, Options.MaxEmbeddedMessages);
            return false;
        }

        Budget.Consume();
        return true;
    }

    /// <summary>Writes the reserved tags a dispatched embedded message needs to continue the count.</summary>
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
