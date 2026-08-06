namespace Rag.NET.Abstractions;

/// <summary>
/// Sanitises the incoming user query before it enters the retrieval pipeline.
/// Implementations should replace injection patterns with [REDACTED] and log a warning.
/// Must never throw — return the original query on failure.
/// </summary>
public interface IQuerySanitiser
{
    /// <summary>Returns a sanitised copy of <paramref name="query"/>. Returns the original text unchanged if there is nothing to redact.</summary>
    string Sanitise(string query);
}
