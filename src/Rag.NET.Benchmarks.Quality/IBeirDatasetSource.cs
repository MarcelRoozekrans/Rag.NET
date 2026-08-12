namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Puts one dataset on disk in BEIR's layout, however that dataset happens to be published.
/// </summary>
/// <remarks>
/// The postcondition is the whole interface: <c>corpus.jsonl</c>, <c>queries.jsonl</c> and
/// <c>qrels/{split}.tsv</c> under the dataset directory. BEIR's own datasets satisfy it by
/// downloading a zip and verifying its MD5 (<see cref="BeirArchiveSource"/>); a dataset published in
/// another shape satisfies it by converting. <see cref="BeirLoader"/> reads a directory and never
/// learns which happened, so nothing downstream — metrics, ranking, sidecars, the run budget —
/// acquires a special case for a dataset that is not shaped like BEIR's.
/// </remarks>
public interface IBeirDatasetSource
{
    /// <summary>
    /// Ensures the dataset is present, in BEIR's layout, at <paramref name="datasetDirectory"/>.
    /// </summary>
    /// <param name="datasetDirectory">
    /// The directory the dataset must end up in. It does not exist yet: <see cref="BeirDatasetCache"/>
    /// deletes any half-finished leftover before calling, so a directory appearing here during the
    /// call is another caller's complete win rather than junk to replace.
    /// </param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    Task PrepareAsync(string datasetDirectory, CancellationToken cancellationToken = default);
}
