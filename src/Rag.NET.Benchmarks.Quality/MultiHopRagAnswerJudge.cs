using System.Text;
using System.Text.RegularExpressions;

namespace Rag.NET.Benchmarks.Quality;

/// <summary>
/// Scores a model's answer to a MultiHop-RAG query against the gold answer, the way the dataset's
/// authors score it — and a stricter way beside it, so the lenience of theirs is visible rather
/// than assumed away.
/// <para>
/// <b>The paper's rule, from <c>qa_evaluate.py</c> in the authors' repository</b>
/// (<c>github.com/yixuantt/MultiHop-RAG</c>, read 2026-08-15): the model is asked to end its reply
/// with <c>The answer to the question is "…"</c>; the quoted text is extracted with a regex, both
/// it and the gold answer are lower-cased and split on whitespace, and the prediction is correct
/// when the two word sets <b>intersect at all</b>. No per-type handling; accuracy is correct over
/// total. That rule is what makes their Table 6 comparable in shape to figures produced here, so
/// it is reproduced exactly, including its lenience: <c>"YouTube Music"</c> matches
/// <c>"YouTube"</c>, and any reply containing the bare token <c>no</c> matches the gold answer
/// <c>no</c> — while <c>"No,"</c> with the comma attached does not, because <c>split()</c> keeps
/// punctuation on its word. Both edges are reproduced as they are.
/// </para>
/// <para>
/// <b>The strict rule is this repository's, and it is reported beside the paper's, never instead
/// of it.</b> Both sides are lower-cased, punctuation is stripped, whitespace collapsed, and the
/// two must be equal. It answers "how much of the paper-rule accuracy is the rule being generous",
/// which the paper-rule figure alone cannot say.
/// </para>
/// <para>
/// <b>Where the model does not use the sentence, the whole reply is scored.</b> The authors' script
/// extracts the quoted answer and the summary of it read on 2026-08-15 does not say what it does
/// when the regex finds nothing; scoring the whole reply is the lenient reading under the paper's
/// rule (more words, more chances to intersect) and the strict reading under ours (a whole reply is
/// never equal to a one-word gold answer), and it is stated here so nobody has to guess which was
/// chosen. Every extraction outcome is reported by the caller as a count, so the fraction of
/// replies that followed the instruction is a figure of its own.
/// </para>
/// </summary>
public static partial class MultiHopRagAnswerJudge
{
    /// <summary>
    /// The sentence the model is instructed to end with, and the regex the authors' script extracts
    /// it with — the quoted text up to the closing quote, first occurrence. (Written as `[^"]*` rather
    /// than the script's `.*?`: same match on every reply that closes its quote, and it lets the
    /// regex compile without backtracking.)
    /// </summary>
    [GeneratedRegex("The answer to the question is \"(?<answer>[^\"]*)\"", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex AnswerSentence();

    /// <summary>
    /// The instruction appended to every prompt so replies carry the sentence the regex extracts.
    /// </summary>
    public const string AnswerInstruction =
        "End your reply with exactly this sentence, filling in the answer: " +
        "The answer to the question is \"...\"";

    /// <summary>Extracts what the model gave as its answer.</summary>
    /// <param name="reply">The model's whole reply.</param>
    /// <returns>The quoted answer when the reply carries the sentence, else the whole reply trimmed.</returns>
    /// <remarks>Whether the sentence was found is available through <see cref="UsedTheAnswerSentence"/>.</remarks>
    public static string ExtractAnswer(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var match = AnswerSentence().Match(reply);
        return match.Success ? match.Groups["answer"].Value : reply.Trim();
    }

    /// <summary>Reports whether the reply carries the answer sentence the prompt asks for.</summary>
    /// <param name="reply">The model's whole reply.</param>
    /// <returns><see langword="true"/> when the regex finds the sentence.</returns>
    public static bool UsedTheAnswerSentence(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return AnswerSentence().IsMatch(reply);
    }

    /// <summary>
    /// The authors' rule: lower-case both, split on whitespace, correct when any word is shared.
    /// </summary>
    /// <param name="prediction">What the model answered, as <see cref="ExtractAnswer"/> returns it.</param>
    /// <param name="gold">The published answer.</param>
    /// <returns><see langword="true"/> when the two word sets intersect.</returns>
    public static bool MatchesByThePaperRule(string prediction, string gold)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(gold);

        var predicted = new HashSet<string>(
            prediction.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        foreach (var word in gold.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (predicted.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The paper's rule over punctuation-stripped tokens: lower-case both, drop punctuation and
    /// symbols, split, correct when any word is shared.
    /// </summary>
    /// <param name="prediction">What the model answered, as <see cref="ExtractAnswer"/> returns it.</param>
    /// <param name="gold">The published answer.</param>
    /// <returns><see langword="true"/> when the two normalised word sets intersect.</returns>
    /// <remarks>
    /// <b>This is the headline rule, and it exists because the pilot measured why the raw one
    /// cannot be.</b> Over 100 stratified queries on 2026-08-15, <c>openai/gpt-4o-mini</c> put the
    /// full stop inside the quotes — <c>The answer to the question is "Google."</c> — on most
    /// replies, and <see cref="MatchesByThePaperRule"/> then scored every comparison query in every
    /// arm at exactly 0.0000 while the strict rule, which strips punctuation, scored the same
    /// replies at 0.18–0.52: the raw rule was measuring the model's punctuation habit, not its
    /// answers. The authors' models evidently did not do this; the rule's intent — a
    /// straightforward accuracy over one-to-three-word answers — is unchanged by stripping the
    /// punctuation before the split, and that is the only change. The raw rule is still computed
    /// and printed beside this one, so the gap is a number rather than a footnote.
    /// </remarks>
    public static bool MatchesByThePaperRuleIgnoringPunctuation(string prediction, string gold)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(gold);

        var predicted = new HashSet<string>(
            Normalise(prediction).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        foreach (var word in Normalise(gold).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (predicted.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The strict rule: lower-cased, punctuation stripped, whitespace collapsed, and equal.
    /// </summary>
    /// <param name="prediction">What the model answered, as <see cref="ExtractAnswer"/> returns it.</param>
    /// <param name="gold">The published answer.</param>
    /// <returns><see langword="true"/> when the normalised forms are identical.</returns>
    public static bool MatchesStrictly(string prediction, string gold)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        ArgumentNullException.ThrowIfNull(gold);

        return string.Equals(Normalise(prediction), Normalise(gold), StringComparison.Ordinal);
    }

    /// <summary>Lower-cases, drops punctuation and symbols, and collapses whitespace to single spaces.</summary>
    private static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune))
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }
}
