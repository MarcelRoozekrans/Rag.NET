using System.Text;
using Microsoft.ML.Tokenizers;

namespace Rag.NET.GraphRag.LocalSearch;

/// <summary>
/// Renders one delimited section — the <c>-----Name-----</c> banner, a header row, and rows added
/// until the next one would not fit.
/// </summary>
/// <remarks>
/// <para>
/// <b>The stopping rule is break, not skip, and that is a deliberate difference from
/// <c>ContextBudgetBehavior</c>.</b> That behaviour <i>skips</i> an oversized chunk and keeps
/// scanning, because it is packing a budget with independently-ranked chunks and a later short one
/// is a free win. Upstream's context tables <c>break</c>: the first row that does not fit ends the
/// section, and everything after it is dropped whether it would have fitted or not.
/// </para>
/// <para>
/// Faithfulness is the reason, and it is not a preference. These rows are not independent — a
/// relationship table that silently omits its third row and keeps its fourth reads as though the
/// third does not exist, and the prompt has no way to tell truncation from absence. Upstream
/// truncates a prefix, so a reader can at least know that what it has is the top of the ranking.
/// </para>
/// </remarks>
internal sealed class ContextTable
{
    /// <summary>
    /// The same encoding <c>ContextBudgetBehavior</c> and <c>ConversationMemoryPipeline</c> count
    /// with. Two token budgets over two halves of one prompt disagreeing about what a token is
    /// would make the combined total meaningless — and this one is a budget the specification
    /// states in tokens, so it is measured in tokens rather than estimated from characters.
    /// </summary>
    private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    private readonly StringBuilder _text = new();
    private readonly string _delimiter;
    private readonly int _budget;
    private int _tokens;
    private int _rendered;
    private bool _stopped;

    /// <summary>Starts a section and writes its banner and header row.</summary>
    /// <param name="name">Section name, as the prompt expects to read it.</param>
    /// <param name="header">Column names.</param>
    /// <param name="delimiter">Column separator.</param>
    /// <param name="budget">Tokens this section may spend, banner and header included.</param>
    internal ContextTable(string name, IReadOnlyList<string> header, string delimiter, int budget)
    {
        _delimiter = delimiter;
        _budget = budget;

        _text.Append("-----").Append(name).Append("-----").Append('\n');
        _text.Append(string.Join(delimiter, header)).Append('\n');

        // Counted against the budget, as upstream counts it: a section whose header alone exceeds
        // its allowance renders nothing, rather than one row over.
        _tokens = s_tokenizer.CountTokens(_text.ToString());
    }

    /// <summary>Rows written so far.</summary>
    internal int Rendered => _rendered;

    /// <summary>Tokens spent so far, banner and header included.</summary>
    internal int Tokens => _tokens;

    /// <summary>Whether a row has already failed to fit, ending the section.</summary>
    internal bool Stopped => _stopped;

    /// <summary>Appends a row if it fits; otherwise ends the section.</summary>
    /// <remarks>
    /// <b>The latch below is the authority, and callers also break out of their loops on a
    /// <see langword="false"/> return.</b> That is redundant on purpose — the caller's break only
    /// saves iterating a selection that can run to tens of thousands — but the redundancy is worth
    /// stating, because it makes the stop rule survive a mutation to either half alone. Anyone
    /// checking that a test defends this rule has to remove both, and will otherwise conclude the
    /// test is vacuous when it is not.
    /// </remarks>
    /// <param name="fields">Cell values, in header order.</param>
    /// <returns>Whether the row was written.</returns>
    internal bool TryAdd(params string[] fields)
    {
        if (_stopped)
        {
            return false;
        }

        var row = string.Join(_delimiter, fields) + "\n";
        var cost = s_tokenizer.CountTokens(row);

        if (_tokens + cost > _budget)
        {
            _stopped = true;
            return false;
        }

        _text.Append(row);
        _tokens += cost;
        _rendered++;
        return true;
    }

    /// <summary>The rendered section, or an empty string when no row was written.</summary>
    /// <remarks>
    /// A banner and a header with nothing under them is worse than nothing: it tells the model the
    /// section exists and is empty, which is a claim about the graph rather than about the budget.
    /// </remarks>
    /// <returns>The section text.</returns>
    internal string Render() => _rendered == 0 ? string.Empty : _text.ToString();

    /// <summary>Counts tokens in arbitrary text with the section tokenizer.</summary>
    /// <param name="text">Text to count.</param>
    /// <returns>Token count.</returns>
    internal static int CountTokens(string text) => s_tokenizer.CountTokens(text);
}
