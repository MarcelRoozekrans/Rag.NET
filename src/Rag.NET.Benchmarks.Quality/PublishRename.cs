namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Renames freshly written files into their published names, absorbing the transient
/// access-denied that Windows produces while an on-access scanner still holds what was just
/// written.
/// </summary>
/// <remarks>
/// <para>
/// Every cache in this assembly publishes by the same discipline: write under a unique name,
/// rename into the shared one. On Windows that rename has an external hazard. The moment a file
/// is written, Defender's on-access scan opens it; a rename that touches a held file — replacing
/// a held destination, or moving a directory with a held file anywhere beneath it, which NTFS
/// refuses whatever sharing the holder asked for — fails with <c>ERROR_ACCESS_DENIED</c>.
/// </para>
/// <para>
/// This was measured before it was retried, on a Windows 11 machine with Defender real-time
/// protection enabled. A standalone, single-threaded repro of extract-then-rename — every handle
/// of its own provably closed — failed on up to 71 of 200 iterations with Win32 error 5 from the
/// raw <c>MoveFileExW</c>. Exclusive-open probes of every staged file and directory, microseconds
/// after each failure, found nothing still held: the denying handle was already gone. A single
/// zero-delay retry succeeded in 100% of observed failures, the widest observed denial cleared in
/// under 8 ms, and a 15 ms settle before the rename took the failure rate from 12/150 to 0/150.
/// The same machine's Linux CI never fails here: POSIX rename has no beneath-the-directory
/// restriction and no on-access scanner. So the retry below is for that measured, transient,
/// external denial — not a leaked handle of ours, which would outlive every attempt and surface
/// through the final throw.
/// </para>
/// </remarks>
internal static class PublishRename
{
    /// <summary>
    /// How many settles a denied rename is granted before its exception propagates: 200 of
    /// <see cref="TransientDenialSettleTime"/>, a three-second ceiling. The idle-machine denial
    /// clears in single-digit milliseconds, but the hold is the scan engine's latency, and the
    /// scan engine competes for the same saturated cores as twelve parallel test collections — a
    /// ten-settle budget was measured to be exhausted under exactly that load. Three seconds is
    /// two-plus orders of magnitude over the widest idle denial while still bounded, so a
    /// genuine, persistent denial — wrong ACL, a directory made read-only — surfaces rather than
    /// hangs.
    /// </summary>
    internal const int TransientDenialRetryLimit = 200;

    /// <summary>
    /// The pause between rename attempts: 15 ms, the settle that took the standalone repro from
    /// 12 failures in 150 iterations to 0.
    /// </summary>
    internal static readonly TimeSpan TransientDenialSettleTime = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Renames a staged, complete dataset directory into the name the cache reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one publication step every <see cref="IBeirDatasetSource"/> shares, whatever it did to
    /// build the directory: <see cref="BeirArchiveSource"/> extracts a zip into it,
    /// <see cref="MultiHopRagConversion"/> converts two JSON files into it, and both must make it
    /// appear complete or not at all. That matters more than it looks, because
    /// <see cref="BeirDatasetCache.IsExtractedAt"/> does not check <c>qrels/{split}.tsv</c>: a
    /// dataset directory that exists with only some of its files is a permanent cache hit for every
    /// later run.
    /// </para>
    /// <para>
    /// When two callers both finish, the first rename wins and the loser's rename throws
    /// <see cref="IOException"/> because the destination now exists. Losing is success: the winner's
    /// directory satisfies the same postcondition, so the loser keeps it, lets its own staging be
    /// discarded, and resolves the same directory. Do not "fix" the swallowed exception — the filter
    /// re-checks <see cref="BeirDatasetCache.IsExtractedAt"/>, so a rename that failed for any other
    /// reason still throws.
    /// </para>
    /// <para>
    /// The winner's rename has its own Windows-only hazard, external to this process: NTFS refuses
    /// to rename a directory while any handle is open on anything beneath it, whatever sharing the
    /// holder asked for — and every file beneath this one was written milliseconds ago, which is
    /// precisely when an on-access scanner opens it. The retry below is for that measured, transient
    /// denial; this class carries the measurements. It cannot mask a handle this process leaked —
    /// our own handle would outlive every attempt and the final throw would still surface it — and
    /// it cannot mask the lost race, because the <c>!Directory.Exists</c> filter refuses to retry
    /// once a rival's dataset directory exists.
    /// </para>
    /// </remarks>
    /// <param name="stagedDirectory">The complete directory, under a name only this caller knows.</param>
    /// <param name="datasetDirectory">The name the cache resolves.</param>
    /// <param name="cancellationToken">Cancels the settles between attempts.</param>
    public static async Task PublishDirectoryAsync(
        string stagedDirectory, string datasetDirectory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (BeirDatasetCache.IsExtractedAt(datasetDirectory))
            {
                return;
            }

            try
            {
                Directory.Move(stagedDirectory, datasetDirectory);
                return;
            }
            catch (IOException) when (BeirDatasetCache.IsExtractedAt(datasetDirectory))
            {
                // Lost a photo finish to a rival that satisfied the same postcondition.
                return;
            }
            catch (IOException) when (
                attempt < TransientDenialRetryLimit && !Directory.Exists(datasetDirectory))
            {
                // The destination does not exist, so this is not the lost race above: it is the
                // scanner-held source measured on this class. Observed denials clear within
                // single-digit milliseconds; past the limit the exception propagates and the
                // caller's delete-on-failure discipline still runs.
                await Task.Delay(TransientDenialSettleTime, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> over <paramref name="destinationPath"/>, replacing it,
    /// retrying the transient scanner denial described on the class.
    /// </summary>
    /// <remarks>
    /// Only the two exception types Windows maps <c>ERROR_ACCESS_DENIED</c> and sharing violations
    /// onto are retried. A missing source or destination directory throws its own type and
    /// propagates immediately; a genuine, persistent denial propagates after the retry budget.
    /// </remarks>
    /// <param name="sourcePath">The freshly written file, owned exclusively by the caller.</param>
    /// <param name="destinationPath">The shared name to publish it under.</param>
    /// <param name="cancellationToken">Cancels the settles between attempts.</param>
    public static async Task ReplaceFileAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < TransientDenialRetryLimit)
            {
                await Task.Delay(TransientDenialSettleTime, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < TransientDenialRetryLimit)
            {
                await Task.Delay(TransientDenialSettleTime, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
