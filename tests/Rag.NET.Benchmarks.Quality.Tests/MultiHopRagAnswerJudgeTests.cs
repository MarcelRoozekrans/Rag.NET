using Xunit;

namespace Rag.NET.Benchmarks.Quality.Tests;

/// <summary>
/// Pins the two answer-scoring rules Phase 5.2.2 reports — the MultiHop-RAG authors' and the strict
/// one beside it — on hand-worked cases, so a figure over 2,255 queries is a sum of decisions each
/// of which is asserted here.
/// </summary>
public sealed class MultiHopRagAnswerJudgeTests
{
    [Theory]
    [InlineData("Based on the articles, The answer to the question is \"YouTube\".", "YouTube", true)]
    [InlineData("The answer to the question is \"YouTube\" and I am confident.", "YouTube", true)]
    [InlineData("I think it is YouTube.", "I think it is YouTube.", false)]
    [InlineData("The answer to the question is \"before\"\nThe answer to the question is \"after\"", "before", true)]
    public void ExtractAnswer_TakesTheFirstQuotedAnswerSentence_OrTheWholeReply(
        string reply, string expected, bool usedSentence)
    {
        Assert.Equal(expected, MultiHopRagAnswerJudge.ExtractAnswer(reply));
        Assert.Equal(usedSentence, MultiHopRagAnswerJudge.UsedTheAnswerSentence(reply));
    }

    [Theory]
    // The authors' rule is any shared word after lower-casing, and it is lenient by design.
    [InlineData("YouTube", "YouTube", true)]
    [InlineData("youtube", "YouTube", true)]
    [InlineData("YouTube Music", "YouTube", true)]
    [InlineData("No the two disagree", "no", true)]
    // Punctuation stays attached to its word under split(), so this does NOT match — faithfully.
    [InlineData("No, the two disagree", "no", false)]
    [InlineData("Insufficient information", "Insufficient information", true)]
    [InlineData("Yes", "no", false)]
    [InlineData("Google", "YouTube", false)]
    [InlineData("", "YouTube", false)]
    public void MatchesByThePaperRule_IsAnySharedWordAfterLowerCasing(string prediction, string gold, bool expected)
    {
        Assert.Equal(expected, MultiHopRagAnswerJudge.MatchesByThePaperRule(prediction, gold));
    }

    [Theory]
    [InlineData("YouTube", "YouTube", true)]
    [InlineData("youtube.", "YouTube", true)]
    [InlineData("  You Tube ", "You Tube", true)]
    [InlineData("YouTube Music", "YouTube", false)]
    [InlineData("No, the two disagree", "no", false)]
    [InlineData("Insufficient information.", "Insufficient information", true)]
    public void MatchesStrictly_IsNormalisedEquality(string prediction, string gold, bool expected)
    {
        Assert.Equal(expected, MultiHopRagAnswerJudge.MatchesStrictly(prediction, gold));
    }

    [Fact]
    public void TheTwoRules_DisagreeExactlyWhereThePaperRuleIsGenerous()
    {
        // The reason both are reported: the strict rule can only be true where the paper rule is,
        // and the gap between them is the lenience made visible.
        const string Prediction = "The Verge and TechCrunch";
        const string Gold = "TechCrunch";

        Assert.True(MultiHopRagAnswerJudge.MatchesByThePaperRule(Prediction, Gold));
        Assert.False(MultiHopRagAnswerJudge.MatchesStrictly(Prediction, Gold));
    }
}
