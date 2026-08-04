namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>UseCostBudgeting</c>: user-supplied prices (there is no built-in price
/// table — provider prices churn) and spend limits. At least one of
/// <see cref="DailyLimit"/>/<see cref="MonthlyLimit"/> must be set; prices must be
/// non-negative. All monetary values share whatever currency the prices are quoted in.
/// </summary>
public sealed class CostBudgetOptions
{
    /// <summary>
    /// The historical default of <see cref="DatabasePath"/>, kept so <c>UseCostBudgeting</c>
    /// can tell "left alone" apart from "explicitly configured" and fail loudly on the latter.
    /// </summary>
    internal const string DefaultDatabasePath = "rag-cost-ledger.db";

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

    /// <summary>
    /// No longer read: the SQLite cost ledger moved to the <c>Rag.NET.Storage.Sqlite</c>
    /// package, whose <c>UseSqliteCostLedger(dbPath)</c> takes the path directly, and
    /// <c>UseCostBudgeting</c> defaults to an in-memory ledger. Setting this to anything
    /// other than its default makes <c>UseCostBudgeting</c> throw rather than silently
    /// hand out a non-persistent ledger the caller did not ask for.
    /// </summary>
    public string DatabasePath { get; set; } = DefaultDatabasePath;
}
