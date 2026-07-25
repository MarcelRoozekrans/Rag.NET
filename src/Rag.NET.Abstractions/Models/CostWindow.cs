namespace Rag.NET.Models;

/// <summary>The rolling window a spend query aggregates over (UTC calendar based).</summary>
public enum CostWindow
{
    /// <summary>The current UTC calendar day.</summary>
    Day,

    /// <summary>The current UTC calendar month (first of the month through today).</summary>
    Month,
}
