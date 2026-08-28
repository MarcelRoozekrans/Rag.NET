using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Asserts every answer arm has a recorded reproduction, in the fast tier, in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and what it costs to not have it.</b>
/// <see cref="MultiHopRagAnswerReproduction"/> already refuses an arm with no recorded figure — but
/// the only thing that consults it is <see cref="MultiHopRagAnswerReproduction.AssertReproduces"/>,
/// called from inside <see cref="BeirGraphRagAnswerTests"/>. So the check fires **at the end of a
/// full run**: 2,556 queries, five arms, 22,230 answer requests, <b>1 h 33 m</b>. That is when the
/// <c>filtered</c> arm added in #274 was found to have no pin — after the measurement, not before
/// it.
/// </para>
/// <para>
/// The ranking table does not have this problem. <c>BeirReproductionTests</c> loops every descriptor
/// against every protocol and calls <c>BeirReproduction.RequireRecordedCase</c> directly, so a
/// missing entry fails in the fast tier on every push.
/// <see cref="MultiHopRagAnswerReproduction.RequireRecordedCase"/> was written for exactly that
/// purpose and <b>nothing called it</b> — an asymmetry between the two tables that only shows up
/// when someone adds an arm.
/// </para>
/// <para>
/// This closes it. An arm added without a pin now fails in milliseconds on the pull request that
/// adds it, rather than ninety minutes into the run that was supposed to produce its number.
/// </para>
/// </remarks>
public sealed class MultiHopRagAnswerReproductionTests
{
    /// <remarks>
    /// Iterates <see cref="AnswerArm.All"/> rather than a hand-written list, so a new arm is
    /// covered by the act of declaring it. A list here would be a second place to forget.
    /// </remarks>
    [Fact]
    public void EveryAnswerArmHasARecordedReproduction()
    {
        Assert.NotEmpty(AnswerArm.All);

        foreach (var arm in AnswerArm.All)
        {
            // Throws with the guidance the full run would have given, only sooner:
            // "add an entry, empty if it has never run, and the first full run fills it."
            MultiHopRagAnswerReproduction.RequireRecordedCase("multihop-rag", arm);
        }
    }

    /// <remarks>
    /// The count is asserted as well as the presence, because a duplicate entry for one arm would
    /// satisfy the loop above while leaving another arm unpinned — <c>Find</c> returns the first
    /// match, so a table with two <c>dense</c> rows and no <c>local</c> row still passes a
    /// per-arm existence check on <c>dense</c> and fails only on <c>local</c>. Pinning the count
    /// makes the table's shape explicit rather than inferred from a search.
    /// </remarks>
    [Fact]
    public void TheArmsAndTheTableAgreeOnHowManyThereAre()
    {
        Assert.Equal(15, AnswerArm.All.Count);
    }
}
