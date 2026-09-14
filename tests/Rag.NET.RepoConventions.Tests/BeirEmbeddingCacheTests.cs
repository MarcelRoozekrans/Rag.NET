using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins the embedding cache step, including the one requirement it holds opposite to
/// <see cref="BeirCorpusCacheTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The corpus cache removed the nightly download; this step removes the
/// recomputation, which is the larger cost — the two cells <c>BeirRunBudget</c> marks
/// <c>FitsTheNightly</c> embed roughly 29,000 texts on a CPU runner and account for most of the
/// job's measurement time.
/// </para>
/// <para>
/// <b>The asymmetry is the point.</b> This step <b>requires</b> <c>restore-keys</c>; the corpus
/// step <b>forbids</b> them. That looks like an inconsistency waiting to be tidied, which is
/// exactly why it is asserted. <c>EmbeddingCache</c> addresses entries by SHA-256 over the model
/// identity and the text, so a restored entry is either keyed by the exact text being embedded — in
/// which case it is that text's vector — or is never looked up. A stale corpus measures; a stale
/// vector cannot be read by mistake.
/// </para>
/// <para>
/// <b>And the revision in the key is load-bearing, not decoration.</b>
/// <c>BeirHarness.ModelIdentity</c> names the model but not its revision, so bumping
/// <c>MINILM_REVISION</c> changes no cache key at all. Without the revision in the Actions key, the
/// <c>restore-keys</c> this file also requires would hand a bumped run the previous model's vectors
/// and every one would read as a hit. The workflow already treats the revision as identity-bearing:
/// it pins it and checks a SHA-256 against it, on the stated grounds that a silently different
/// model moves the parity number by an amount nobody could attribute. This key is what makes the
/// cache agree with that.
/// </para>
/// </remarks>
public sealed class BeirEmbeddingCacheTests
{
    /// <summary>The step this guard is about, matched on its <c>name:</c>.</summary>
    private const string StepName = "Cache the BEIR embedding vectors";

    /// <summary>The two fields whose contents decide which vectors a run writes and reads.</summary>
    private static readonly string[] TheTwoFieldsThatDecideWhatIsReadAndWritten =
    [
        "key:",
        "restore-keys:",
    ];

    /// <summary>The vectors are cached, and only the vectors.</summary>
    [Fact]
    public void TheNightlyCachesTheEmbeddingVectors()
    {
        var step = ReadStep();

        Assert.Contains("uses: actions/cache", step, StringComparison.Ordinal);

        Assert.True(
            step.Contains("${{ runner.temp }}/beir/embeddings", StringComparison.Ordinal),
            "The embedding cache no longer names the directory EmbeddingCache writes to, so every " +
            "nightly run re-embeds what the previous one already computed.");
    }

    /// <summary>The model revision salts BOTH the key and the prefix it falls back on.</summary>
    /// <remarks>
    /// <b>Asserted line by line, and that is not pedantry — a whole-step search passed this when it
    /// should have failed.</b> Removing the revision from <c>key:</c> alone left it present on
    /// <c>restore-keys:</c>, so a <c>Contains</c> over the step still matched while the property was
    /// gone. Both lines have to carry it independently: the key decides what a run writes, the
    /// prefix decides what it is willing to read, and a bumped revision that still reads the old
    /// prefix restores the previous model's vectors however the key is spelled.
    /// </remarks>
    [Fact]
    public void BothTheKeyAndTheFallbackPrefixCarryTheModelRevision()
    {
        var step = ReadStep();

        foreach (var field in TheTwoFieldsThatDecideWhatIsReadAndWritten)
        {
            var line = LineStartingWith(step, field);

            Assert.True(
                line.Length > 0,
                $"The embedding cache step has no '{field}' line, so this guard cannot check it.");

            Assert.True(
                line.Contains("MINILM_REVISION", StringComparison.Ordinal),
                $"The embedding cache's '{field}' no longer carries MINILM_REVISION. " +
                "BeirHarness.ModelIdentity names the model but not its revision, so nothing else " +
                "distinguishes one export's vectors from another's: a bumped pin would restore the " +
                "old model's vectors and every one would read as a cache hit. The parity numbers " +
                "would then be the previous model's, reported under the new one. Both lines need " +
                "it — the key decides what is written, the prefix decides what is read.");
        }
    }

    /// <summary>This step requires what the corpus step forbids. Asserted so a tidy-up cannot.</summary>
    [Fact]
    public void TheKeyRotatesAndFallsBackOnAPrefix()
    {
        var step = ReadStep();

        Assert.True(
            step.Contains("restore-keys", StringComparison.Ordinal),
            "The embedding cache has lost its restore-keys. A cache entry is immutable once " +
            "written, so without a prefix fallback every run starts cold and the step buys " +
            "nothing. This requirement is the deliberate opposite of the corpus cache's, which " +
            "BeirCorpusCacheTests forbids restore-keys entirely: entries here are content-" +
            "addressed on model identity and text, so a stale one cannot be read by mistake.");

        Assert.True(
            step.Contains("github.run_id", StringComparison.Ordinal),
            "The embedding cache key no longer rotates. A fixed key is written once and never " +
            "updated, so the cache would freeze whatever the first night embedded and silently " +
            "stop growing — which looks identical to a cache that is working.");
    }

    /// <summary>Gets the one step line beginning with <paramref name="prefix"/>.</summary>
    /// <param name="step">The step body.</param>
    /// <param name="prefix">The YAML field, including its colon.</param>
    /// <returns>The whole line, or empty when no line begins with it.</returns>
    private static string LineStartingWith(string step, string prefix)
    {
        foreach (var line in step.Split(Environment.NewLine))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return string.Empty;
    }

    /// <summary>Reads the cache step's body.</summary>
    /// <returns>The step's lines, newline-joined.</returns>
    private static string ReadStep()
    {
        var step = TestProject.ReadWorkflowStep(TestProject.WorkflowPath("nightly.yml"), StepName);

        Assert.True(
            step.Length > 0,
            $"nightly.yml has no step named \"{StepName}\". Either the embedding cache was " +
            "removed — putting the whole embedding cost back on every nightly run — or it was " +
            "renamed, which disarms this guard. Renaming is fine; update the name here with it.");

        return step;
    }
}
