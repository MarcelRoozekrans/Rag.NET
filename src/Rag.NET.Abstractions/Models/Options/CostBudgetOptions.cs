namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>UseCostBudgeting</c>: user-supplied prices (there is no built-in price
/// table — provider prices churn) and spend limits. At least one of
/// <see cref="DailyLimit"/>/<see cref="MonthlyLimit"/> must be set; prices must be
/// non-negative. All monetary values share whatever currency the prices are quoted in.
/// </summary>
public sealed class CostBudgetOptions
{
    /// <summary>Price per 1,000,000 input (prompt) tokens for chat calls. Must be >= 0.</summary>
    public decimal InputPricePerMTokens { get; set; }

    /// <summary>Price per 1,000,000 output (completion) tokens for chat calls. Must be >= 0.</summary>
    public decimal OutputPricePerMTokens { get; set; }

    /// <summary>Price per 1,000,000 input tokens for embedding calls. Must be >= 0.</summary>
    public decimal EmbeddingPricePerMTokens { get; set; }

    /// <summary>
    /// Spend limit for the current UTC calendar day; once recorded spend reaches it, calls
    /// throw <see cref="BudgetExceededException"/>. <see langword="null"/> (default) means
    /// no daily limit.
    /// </summary>
    public decimal? DailyLimit { get; set; }

    /// <summary>
    /// Spend limit for the current UTC calendar month; once recorded spend reaches it, calls
    /// throw <see cref="BudgetExceededException"/>. <see langword="null"/> (default) means
    /// no monthly limit.
    /// </summary>
    public decimal? MonthlyLimit { get; set; }
}
