using System.Text.Json;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Evaluation.Internal;

namespace Rag.NET.Evaluation.Ragas.Judging;

/// <summary>
/// Owns every LLM interaction the RAGAS metrics need: prompting and parsing, over the shared
/// throttled-and-billed caller.
/// </summary>
/// <remarks>
/// Exists so the metrics themselves are arithmetic over judgement arrays. Before Phase 3.1 each
/// evaluator carried its own copy of this plumbing, which is how the same JSON-parse defect came
/// to exist twice and the same brittle verdict parsing three times.
/// <para>
/// The throttling and cost recording moved down into <see cref="EvaluationChatCaller"/> so the
/// dataset builder could share them instead of growing a second copy. What is left here is what is
/// genuinely RAGAS-specific: verdict parsing, JSON extraction and fence stripping. The caller is
/// constructed once per judge, and a suite builds one judge, so the ceiling stays per run.
/// </para>
/// </remarks>
/// <param name="chatClient">The model asked for judgements.</param>
/// <param name="options">Run tuning: the shared concurrency ceiling and the token prices.</param>
/// <param name="costLedger">Optional ledger; when absent, nothing is billed.</param>
internal sealed class RagasJudge(
    IChatClient chatClient,
    RagasOptions options,
    ICostLedger? costLedger = null)
{
    private readonly EvaluationChatCaller _caller = new(chatClient, options, costLedger);

    /// <summary>Asks for a yes/no judgement.</summary>
    /// <param name="systemPrompt">The instruction that frames the judgement.</param>
    /// <param name="userPrompt">The material being judged.</param>
    /// <param name="cancellationToken">Token to cancel the call.</param>
    public async Task<Verdict> ClassifyAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await _caller.CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        return ParseVerdict(reply);
    }

    /// <summary>Judges many items under the shared concurrency ceiling, preserving input order.</summary>
    /// <param name="systemPrompt">The instruction that frames every judgement.</param>
    /// <param name="items">The items to judge, in the order the result must preserve.</param>
    /// <param name="toUserPrompt">Builds the user prompt for one item.</param>
    /// <param name="cancellationToken">Token to cancel the calls.</param>
    public async Task<IReadOnlyList<Verdict>> ClassifyManyAsync(
        string systemPrompt,
        IReadOnlyList<string> items,
        Func<string, string> toUserPrompt,
        CancellationToken cancellationToken)
    {
        var tasks = new Task<Verdict>[items.Count];
        for (var i = 0; i < items.Count; i++)
            tasks[i] = ClassifyAsync(systemPrompt, toUserPrompt(items[i]), cancellationToken);

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Asks for a JSON array of strings, distinguishing "empty" from "unreadable".</summary>
    /// <remarks>
    /// Extraction is <see cref="LlmJsonPayloadKind.FencedOnly"/> on purpose: a fence is honoured
    /// wherever it sits — models emit fenced JSON constantly outside structured-output mode, and
    /// a preamble before the fence is the common decoration — but no attempt is made to salvage
    /// bare JSON out of prose by bracket-scanning. That would start scoring replies the model
    /// never intended as JSON, which is guessing; rejecting them keeps the sample excluded
    /// rather than wrongly scored. The bare-scan refusal is pinned by
    /// <c>ExtractListAsync_MalformedJson_ReportsFailureInsteadOfEmpty</c>.
    /// </remarks>
    /// <param name="systemPrompt">The instruction that asks for the array.</param>
    /// <param name="userPrompt">The material to extract from.</param>
    /// <param name="cancellationToken">Token to cancel the call.</param>
    public async Task<ExtractionResult> ExtractListAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var reply = await _caller.CompleteAsync(systemPrompt, userPrompt, cancellationToken).ConfigureAwait(false);
        var payload = LlmJsonExtractor.Extract(reply, LlmJsonPayloadKind.FencedOnly);
        if (string.IsNullOrWhiteSpace(payload))
            return ExtractionResult.Failed();

        try
        {
            var items = JsonSerializer.Deserialize(payload, RagJsonSerializerContext.Default.ListString);

            // A null element is a half-produced reply, not a datum. Filtering it silently would
            // hand the caller an empty claim the model then answers arbitrarily, and that verdict
            // would land in the denominator — the fabricated score this phase exists to remove,
            // re-entering through a different door.
            return items is null || items.Exists(static item => item is null)
                ? ExtractionResult.Failed()
                : ExtractionResult.Success(items);
        }
        catch (JsonException)
        {
            return ExtractionResult.Failed();
        }
    }

    /// <summary>
    /// Exact match after trimming whitespace and the punctuation models decorate a one-word answer
    /// with. Anything else is <see cref="Verdict.Unparseable"/> rather than a guess.
    /// </summary>
    /// <remarks>
    /// The trim set covers <c>**Yes**</c> and <c>"yes"</c> — markdown emphasis and the quotes a
    /// model leaves behind when it half-honours a JSON instruction. Every verdict wrongly rejected
    /// shrinks the denominator, which is its own way of producing a quietly wrong number. It stops
    /// well short of prose: <c>Yes, but only partially.</c> stays unparseable, because it is.
    /// </remarks>
    private static Verdict ParseVerdict(string reply)
    {
        var trimmed = reply.Trim().Trim('.', '!', '*', '"', ',', ':', ' ');
        if (string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase))
            return Verdict.Yes;

        return string.Equals(trimmed, "no", StringComparison.OrdinalIgnoreCase)
            ? Verdict.No
            : Verdict.Unparseable;
    }
}
