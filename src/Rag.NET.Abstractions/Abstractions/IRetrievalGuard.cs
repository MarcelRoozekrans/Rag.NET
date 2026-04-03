using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Inspects and optionally redacts retrieved chunks before they enter the answer prompt.
/// Implementations should replace injection patterns with [REDACTED] — never drop without logging.
/// Must never throw — return the original results on failure.
/// </summary>
public interface IRetrievalGuard
{
    IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results);
}
