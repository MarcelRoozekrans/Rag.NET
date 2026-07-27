namespace Rag.NET.Models;

/// <summary>
/// One usage record for the cost ledger: the units consumed (tokens for token-priced calls,
/// pages for per-page calls — measured or estimated) and the cost computed by the caller from
/// its configured prices.
/// </summary>
/// <remarks>
/// Only <see cref="Kind"/> and <see cref="Cost"/> are required. The unit counters default to
/// zero so each kind of call populates only the ones it is actually billed on: a
/// <see cref="CostKind.Ocr"/> entry sets <see cref="Pages"/> and leaves the token counts at
/// zero rather than fabricating a token count for an API that never reports one.
/// </remarks>
public sealed record CostEntry
{
    /// <summary>The kind of call that produced this entry.</summary>
    public required CostKind Kind { get; init; }

    /// <summary>Input (prompt) tokens consumed. Zero for calls not priced per token.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output (completion) tokens produced. Zero for calls not priced per token.</summary>
    public long OutputTokens { get; init; }

    /// <summary>
    /// Pages processed by a per-page API such as document OCR. Zero for calls not priced
    /// per page. This is the count the provider <i>billed</i> for, which is not necessarily
    /// the number of pages the caller ended up using.
    /// </summary>
    public int Pages { get; init; }

    /// <summary>
    /// Cost of the call, computed by the caller from its configured prices
    /// (the ledger never prices tokens itself).
    /// </summary>
    public required decimal Cost { get; init; }
}
