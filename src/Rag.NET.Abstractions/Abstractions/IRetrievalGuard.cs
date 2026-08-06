using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Inspects and optionally redacts retrieved chunks before they enter the answer prompt.
/// Implementations should replace injection patterns with [REDACTED] — never drop without logging.
/// Must never throw — return the original results on failure.
/// </summary>
public interface IRetrievalGuard
{
    /// <summary>
    /// Returns a possibly-redacted copy of <paramref name="results"/>. May return the input
    /// unchanged, a copy with some chunks' text redacted, or a shorter list with chunks removed
    /// entirely, depending on the guard.
    /// </summary>
    IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results);
}
