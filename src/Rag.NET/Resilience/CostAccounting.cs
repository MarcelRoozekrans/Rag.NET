using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using Rag.NET.Abstractions;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Telemetry;

namespace Rag.NET.Resilience;

/// <summary>
/// Shared budget-gate, token-estimation and ledger-recording logic for the cost-tracking
/// decorators. Estimation uses the tiktoken cl100k tokenizer (same counting approach as
/// <c>ConversationMemoryPipeline</c>); a shared static instance because tokenizer
/// construction loads vocabulary data.
/// </summary>
internal static class CostAccounting
{
    private static readonly Tokenizer s_tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    internal static int CountTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : s_tokenizer.CountTokens(text);

    internal static long CountMessageTokens(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            total += CountTokens(message.Text);
        }

        return total;
    }

    /// <summary>
    /// Pre-call budget gate: throws <see cref="BudgetExceededException"/> when the recorded
    /// spend of a configured window has reached its limit (Day checked before Month). A
    /// ledger READ failure degrades to an ungated call with a warning — budget enforcement
    /// is best-effort under storage failure; cancellation still propagates.
    /// </summary>
    internal static async Task EnforceBudgetAsync(
        ICostLedger ledger,
        CostBudgetOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        decimal daySpend = 0m;
        decimal monthSpend = 0m;
        try
        {
            if (options.DailyLimit is not null)
            {
                daySpend = await ledger.GetSpendAsync(CostWindow.Day, cancellationToken).ConfigureAwait(false);
            }

            if (options.MonthlyLimit is not null)
            {
                monthSpend = await ledger.GetSpendAsync(CostWindow.Month, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RagPipelineLog.CostLedgerReadFailed(logger, ex);
            return; // Degraded, never broken: an unreadable ledger must not block calls.
        }

        if (options.DailyLimit is { } dailyLimit && daySpend >= dailyLimit)
        {
            throw new BudgetExceededException(CostWindow.Day, dailyLimit, daySpend);
        }

        if (options.MonthlyLimit is { } monthlyLimit && monthSpend >= monthlyLimit)
        {
            throw new BudgetExceededException(CostWindow.Month, monthlyLimit, monthSpend);
        }
    }

    /// <summary>
    /// Post-call recording: emits the token/cost counters, then appends to the ledger.
    /// A ledger WRITE failure only warns — the (already successful) call never fails
    /// retroactively. Cancellation propagates only when the caller's own token fired.
    /// </summary>
    internal static async Task RecordAsync(
        ICostLedger ledger,
        CostEntry entry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Telemetry first: the counters must not silently drop usage because the ledger is down.
        // "surface" is the unified chat|embedding tag name shared with ragnet.ratelimit.wait.duration.
        var surfaceTag = new KeyValuePair<string, object?>("surface", entry.Kind == CostKind.Chat ? "chat" : "embedding");
        RagTelemetry.LlmTokens.Add(entry.InputTokens,
            new TagList { surfaceTag, new KeyValuePair<string, object?>("direction", "in") });
        RagTelemetry.LlmTokens.Add(entry.OutputTokens,
            new TagList { surfaceTag, new KeyValuePair<string, object?>("direction", "out") });
        RagTelemetry.LlmCost.Add((double)entry.Cost, new TagList { surfaceTag });

        try
        {
            await ledger.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // The caller cancelled and has abandoned the call; nothing to salvage.
        }
        catch (Exception ex)
        {
            RagPipelineLog.CostLedgerRecordFailed(logger, ex);
        }
    }
}
