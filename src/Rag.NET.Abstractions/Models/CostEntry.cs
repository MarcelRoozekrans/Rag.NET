namespace Rag.NET.Models;

/// <summary>
/// One usage record for the cost ledger: token counts (measured or estimated) and the
/// cost computed by the caller from its configured prices.
/// </summary>
public sealed record CostEntry
{
    /// <summary>The kind of call that produced this entry.</summary>
    public required CostKind Kind { get; init; }

    /// <summary>Input (prompt) tokens consumed.</summary>
    public required long InputTokens { get; init; }

    /// <summary>Output (completion) tokens produced.</summary>
    public required long OutputTokens { get; init; }

    /// <summary>
    /// Cost of the call, computed by the caller from its configured prices
    /// (the ledger never prices tokens itself).
    /// </summary>
    public required decimal Cost { get; init; }
}
