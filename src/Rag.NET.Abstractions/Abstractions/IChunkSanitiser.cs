using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Sanitises a text chunk at ingestion time before it is embedded and stored.
/// Implementations should replace injection patterns with [REDACTED] and log a warning.
/// Must never throw — return the original text on failure.
/// </summary>
public interface IChunkSanitiser
{
    string Sanitise(string text, IDictionary<string, string> metadata);
}
