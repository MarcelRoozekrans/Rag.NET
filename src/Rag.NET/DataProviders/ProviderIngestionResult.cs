using Rag.NET.Models;

namespace Rag.NET.DataProviders;

/// <summary>
/// What a completed <see cref="RagPipelineExtensions.IngestFromProviderAsync"/> run did, entry by
/// entry.
/// </summary>
/// <param name="Ingested">Entries that were parsed, chunked and stored.</param>
/// <param name="Skipped">
/// Entries that were <b>already up to date</b> — an ETag or content hash matched, so there was
/// nothing to do. Not a failure.
/// </param>
/// <param name="Failed">
/// Entries that threw. Each contributes one entry to <see cref="Errors"/>.
/// </param>
/// <param name="NotAttempted">
/// Entries the provider listed that this run never reached, because
/// <see cref="Models.Options.IngestionOptions.StopOnFirstError"/> stopped it at the first failure.
/// Empty on every run that went to completion.
/// </param>
/// <param name="Deleted">
/// Documents removed by the cleanup pass. Only <see cref="ProviderEntryOutcome.Id"/> is populated —
/// see the remarks.
/// </param>
/// <param name="Errors">Every error the run collected. See the remarks on counting.</param>
/// <remarks>
/// <para>
/// <b>The four entry lists account for every entry the provider listed</b>, each in exactly one of
/// them: <c>Ingested</c> + <c>Skipped</c> + <c>Failed</c> + <c>NotAttempted</c>. That total is the
/// point of the shape — a run that stops early is otherwise indistinguishable from one that had
/// less to do, because the entries after the failure appear nowhere. They appear in
/// <c>NotAttempted</c>.
/// </para>
/// <para>
/// <b><c>Deleted</c> is not part of that total</b>, and it carries no file name. A deleted document
/// is by definition one the provider no longer lists, so this run never saw an entry for it; the id
/// comes from the hash store's record of an earlier run. That is also why these are
/// <see cref="ProviderEntryOutcome"/> and not <see cref="FileEntry"/> — for a deleted document, no
/// <see cref="FileEntry"/> exists to return.
/// </para>
/// <para>
/// <b><see cref="Errors"/> does not have <see cref="FailedCount"/> entries.</b> It also collects
/// failures that belong to no single entry: an invalid <see cref="Models.Options.IngestionOptions"/>,
/// a provider that faulted while listing, a delete that threw during cleanup, and the explanation
/// when full cleanup was requested but could not run. Read <c>Failed</c> for which entries failed;
/// read <c>Errors</c> for everything that went wrong.
/// </para>
/// <para>
/// <b>Ordering.</b> Under the default <c>MaxDegreeOfParallelism</c> of 1 the lists follow the order
/// the provider listed the entries. Above 1 the order is whatever the parallel run produced.
/// </para>
/// <para>
/// <b>History.</b> <c>Failed</c> was split out of <c>Skipped</c> in #355 — a throwing entry used to
/// be counted as skipped, so a sitemap ingest against a missing index reported fifty skips and no
/// failures. These were counts until #395 asked for the entries themselves, so a caller could say
/// <i>which</i> files were skipped without hooking <see cref="IngestionProgress"/>.
/// </para>
/// </remarks>
public sealed record ProviderIngestionResult(
    IReadOnlyList<ProviderEntryOutcome> Ingested,
    IReadOnlyList<ProviderEntryOutcome> Skipped,
    IReadOnlyList<ProviderEntryOutcome> Failed,
    IReadOnlyList<ProviderEntryOutcome> NotAttempted,
    IReadOnlyList<ProviderEntryOutcome> Deleted,
    IReadOnlyList<RagError> Errors)
{
    /// <summary>Gets the number of entries that were parsed, chunked and stored.</summary>
    public int IngestedCount => Ingested.Count;

    /// <summary>Gets the number of entries that were already up to date.</summary>
    public int SkippedCount => Skipped.Count;

    /// <summary>Gets the number of entries that threw.</summary>
    public int FailedCount => Failed.Count;

    /// <summary>Gets the number of listed entries this run never reached.</summary>
    public int NotAttemptedCount => NotAttempted.Count;

    /// <summary>Gets the number of documents removed by the cleanup pass.</summary>
    public int DeletedCount => Deleted.Count;

    /// <summary>
    /// Gets the number of entries the provider listed and this run accounted for — the sum of
    /// <see cref="IngestedCount"/>, <see cref="SkippedCount"/>, <see cref="FailedCount"/> and
    /// <see cref="NotAttemptedCount"/>. Excludes <see cref="DeletedCount"/>, which counts documents
    /// the provider did not list.
    /// </summary>
    public int ListedCount => IngestedCount + SkippedCount + FailedCount + NotAttemptedCount;
}
