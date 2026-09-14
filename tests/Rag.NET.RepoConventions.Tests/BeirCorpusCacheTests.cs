using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins the three properties of the nightly's BEIR corpus cache that can fail silently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Until
/// <see href="https://github.com/MarcelRoozekrans/Rag.NET/issues/175">#175</see> the corpora were
/// re-downloaded on every nightly run — four BEIR archives and MultiHop-RAG's two Hugging Face
/// files — because nothing cached them. Caching them is one step; keeping it correct is three
/// properties that a reasonable-looking edit removes without any signal.
/// </para>
/// <para>
/// <b>The dangerous one is <c>restore-keys</c>.</b> <c>BeirDatasetCache</c> treats a directory
/// holding <c>corpus.jsonl</c> and <c>queries.jsonl</c> as present and never re-verifies it against
/// the published MD5 — that check happens during download, which a cache hit skips by definition.
/// A prefix-matched restore would therefore serve a corpus pinned to a digest the descriptors no
/// longer name, and every measurement taken over it would be attributed to the new pin. Adding
/// <c>restore-keys</c> is the obvious tidy-up, it looks like the NuGet cache three steps above, and
/// it is the one thing here that must not happen.
/// </para>
/// <para>
/// <b>Why the assertions are scoped to the step.</b> <c>nightly.yml</c> legitimately carries
/// <c>restore-keys</c> for the NuGet cache, so a whole-file search would fail on a correct
/// workflow. The block is cut out by name and the checks run inside it, which also means renaming
/// the step fails this guard loudly rather than quietly disarming it.
/// </para>
/// </remarks>
public sealed class BeirCorpusCacheTests
{
    /// <summary>The step this guard is about, matched on its <c>name:</c>.</summary>
    private const string StepName = "Cache the BEIR corpora";

    /// <summary>The files holding every dataset pin the cache key must track.</summary>
    /// <remarks>
    /// One holds the archive URLs and BEIR's published MD5s; the other holds MultiHop-RAG's pinned
    /// revision, byte lengths and MD5s. A key that tracks one but not the other can serve a corpus
    /// fetched under a pin that has since moved.
    /// </remarks>
    private static readonly string[] FilesHoldingAPin =
    [
        "src/Rag.NET.Benchmarks.Quality/BeirDatasetDescriptor.cs",
        "src/Rag.NET.Benchmarks.Quality/MultiHopRagSource.cs",
    ];

    /// <summary>The corpora are cached, and the embedding vectors beside them are not.</summary>
    [Fact]
    public void TheNightlyCachesTheCorporaAndExcludesTheEmbeddings()
    {
        var step = ReadStep();

        Assert.Contains("uses: actions/cache", step, StringComparison.Ordinal);

        Assert.True(
            step.Contains("${{ runner.temp }}/beir", StringComparison.Ordinal),
            "The BEIR corpus cache no longer names the directory RAGNET_BEIR_CACHE points at, so " +
            "it caches nothing the tests read and every nightly run re-downloads five corpora.");

        Assert.True(
            step.Contains("!${{ runner.temp }}/beir/embeddings", StringComparison.Ordinal),
            "The exclusion for the embeddings subdirectory is gone. EmbeddingCache writes vectors " +
            "under the same root, they grow with every ablation rather than with the corpus list, " +
            "and caching them is a separate decision with its own argument about size and " +
            "eviction. Taking it by accident is not that decision.");
    }

    /// <summary>The key tracks every file that holds a pin.</summary>
    [Fact]
    public void TheCorpusCacheKeyHashesEveryFileThatHoldsAPin()
    {
        var step = ReadStep();
        var root = TestProject.FindRepositoryRoot();

        foreach (var file in FilesHoldingAPin)
        {
            Assert.True(
                File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))),
                $"{file} does not exist, so this guard is asserting against a stale path rather " +
                "than against the files that hold the dataset pins.");

            Assert.True(
                step.Contains(file, StringComparison.Ordinal),
                $"The corpus cache key no longer hashes {file}. A pin changed in a file the key " +
                "does not track is a corpus the cache keeps serving under the old bytes, and " +
                "nothing downstream re-verifies it.");
        }
    }

    /// <summary>No prefix-matched restore, for the reason on the class.</summary>
    [Fact]
    public void TheCorpusCacheHasNoRestoreKeys()
    {
        var step = ReadStep();

        Assert.False(
            step.Contains("restore-keys", StringComparison.Ordinal),
            "The BEIR corpus cache has acquired restore-keys. A prefix match hands back a corpus " +
            "pinned to a digest the descriptors no longer name, and BeirDatasetCache will not " +
            "notice: it checks the published MD5 during download, which a cache hit skips. The " +
            "NuGet cache three steps above may have restore-keys because a stale package resolves " +
            "or does not; a stale corpus measures.");
    }

    /// <summary>Reads the cache step's body.</summary>
    /// <returns>The step's lines, newline-joined.</returns>
    /// <remarks>
    /// The walk lives on <see cref="TestProject.ReadWorkflowStep"/> so this guard and
    /// <see cref="BeirEmbeddingCacheTests"/> cannot disagree about where a step ends — they assert
    /// opposite things about <c>restore-keys</c>, and two readers drifting apart would let both
    /// pass while the workflow satisfied neither.
    /// </remarks>
    private static string ReadStep()
    {
        var step = TestProject.ReadWorkflowStep(TestProject.WorkflowPath("nightly.yml"), StepName);

        Assert.True(
            step.Length > 0,
            $"nightly.yml has no step named \"{StepName}\". Either the BEIR corpus cache was " +
            "removed — putting five corpus downloads back on every nightly run — or it was " +
            "renamed, which disarms this guard. Renaming is fine; update the name here with it.");

        return step;
    }
}
