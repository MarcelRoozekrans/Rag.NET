using System.Globalization;

namespace Rag.NET.Models;

#pragma warning disable RCS1194 // Implement exception constructors — Window/Limit/Spend are the
// primary diagnostic values; a message-only instance would carry misleading defaults.

/// <summary>
/// Thrown by the cost-budgeting decorators when the recorded spend in a window has reached
/// its configured limit: the call is blocked before it starts. Deliberately loud — this is
/// the one resilience feature whose job is to throw.
/// </summary>
/// <param name="window">The exhausted window.</param>
/// <param name="limit">The configured limit for that window.</param>
/// <param name="spend">The recorded spend that reached the limit.</param>
public sealed class BudgetExceededException(CostWindow window, decimal limit, decimal spend)
    : InvalidOperationException(BuildMessage(window, limit, spend))
{
    /// <summary>The exhausted window (<see cref="CostWindow.Day"/> or <see cref="CostWindow.Month"/>).</summary>
    public CostWindow Window { get; } = window;

    /// <summary>The configured limit for <see cref="Window"/>.</summary>
    public decimal Limit { get; } = limit;

    /// <summary>The recorded spend at the time of the check.</summary>
    public decimal Spend { get; } = spend;

    private static string BuildMessage(CostWindow window, decimal limit, decimal spend) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The {window} cost budget is exhausted: recorded spend {spend} has reached the configured limit {limit}. " +
            $"Calls are blocked until the {window} window rolls over (UTC) or the limit is raised.");
}

#pragma warning restore RCS1194
