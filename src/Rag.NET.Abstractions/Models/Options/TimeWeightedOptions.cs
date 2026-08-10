using ZeroAlloc.Validation;

namespace Rag.NET.Models.Options;

/// <summary>
/// Tuning for time-weighted re-scoring: search results are multiplied by an exponential decay
/// factor based on chunk age, so more recent content ranks higher than an equally-similar older
/// match.
/// </summary>
[Validate]
public sealed class TimeWeightedOptions
{
    /// <summary>
    /// Decay constant λ in <c>score × e^(−λ × age_hours)</c>.
    /// Default 0.01 halves relevance after ~69 hours (~3 days).
    /// <para>
    /// Must be zero or positive, and finite — enforced by the validation attributes, which
    /// <c>RagBuilder.UseTimeWeighting</c> runs through the generated
    /// <c>TimeWeightedOptionsValidator</c> at registration. A negative rate turns
    /// <c>e^(−λ × age_hours)</c> into exponential <i>growth</i> with age
    /// (<c>TimeWeightedRetriever.ComputeDecay</c>), so the oldest content in the store would
    /// outrank everything regardless of similarity — recency weighting silently inverted.
    /// Zero is valid and means no decay: every result keeps its original score.
    /// </para>
    /// </summary>
    [GreaterThanOrEqualTo(0.0)]
    [Must(nameof(DecayRateIsFinite), Message = "DecayRate must be a finite number (not NaN or infinity).")]
    public double DecayRate { get; init; } = 0.01;

    /// <summary>Reports whether <see cref="DecayRate"/> is a finite number.</summary>
    /// <param name="value">The <see cref="DecayRate"/> value under validation.</param>
    /// <returns>Whether the value is neither NaN nor infinite.</returns>
    internal bool DecayRateIsFinite(double value) => double.IsFinite(value);

    /// <summary>
    /// Ordered list of <see cref="Rag.NET.Models.TextChunk"/> metadata keys to check when both
    /// reserved timestamp keys — <c>"updated_at"</c>, then <c>"created_at"</c> — are absent or
    /// unparseable (see <c>TimeWeightedRetriever.ResolveTimestamp</c> for the precedence).
    /// First key with a parseable ISO 8601 value wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default covers the timestamp tags the bundled <c>Rag.NET.DataProviders.*</c>
    /// connectors actually write, ordered from broadest coverage to narrowest:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>"updated_at"</c> — Asana, Jira, Notion, and Zendesk (both tickets and articles).
    /// </description></item>
    /// <item><description>
    /// <c>"published_at"</c> — RSS/Atom feeds (<c>Rag.NET.DataProviders.Web</c>'s
    /// <c>RssDataProvider</c>).
    /// </description></item>
    /// <item><description>
    /// <c>"lastmod"</c> — sitemaps (<c>Rag.NET.DataProviders.Web</c>'s
    /// <c>SitemapDataProvider</c>).
    /// </description></item>
    /// <item><description>
    /// <c>"received_at"</c> — Exchange mail (<c>Rag.NET.DataProviders.Microsoft365</c>'s
    /// <c>ExchangeMailDataProvider</c>); a different key from the others because "when this
    /// message arrived" is Exchange's own vocabulary, not a value it copies into
    /// <c>updated_at</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Linear is not included</b> despite writing an <c>updatedAt</c> timestamp: that value
    /// is used only for the connector's <c>ETag</c>/delta-token watermark and is never copied
    /// into a chunk metadata tag, so there is nothing under any key for this list to find.
    /// </para>
    /// <para>
    /// <b><c>"date"</c> is deliberately excluded</b>, even though Gmail, Slack, and Microsoft
    /// Teams all write it. Gmail's <c>"date"</c> is a full timestamp (the message's send time),
    /// but Slack's and Teams' <c>"date"</c> is day-granularity only (each tag groups a whole
    /// day's messages into one document) — mixing the two under one fallback key would apply
    /// decay computed from inconsistent precision depending on which connector produced the
    /// chunk. Worse, <c>"date"</c> is generic enough that a caller's own document metadata may
    /// already use it for something time-weighting should not touch — an invoice date, a due
    /// date — so treating it as a timestamp source by default risks decaying by the wrong
    /// clock entirely. Callers who accept the risk for their own data can opt in with one line:
    /// <c>FallbackMetadataKeys = ["updated_at", "published_at", "lastmod", "received_at", "date"]</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> FallbackMetadataKeys { get; init; } =
        ["updated_at", "published_at", "lastmod", "received_at"];
}
