using Rag.NET.Models;

namespace Rag.NET.Abstractions;

/// <summary>
/// Sanitises a text chunk at ingestion time before it is embedded and stored.
/// Implementations should replace injection patterns with [REDACTED] and log a warning.
/// Must not throw on a failure, such as a failed model call: return the original text instead.
/// Cancellation and a deliberate refusal (<see cref="CallRefusedException"/>, such as a spent
/// cost budget) propagate, so the document fails rather than being stored unsanitised.
/// </summary>
public interface IChunkSanitiser
{
    /// <summary>
    /// Returns a sanitised copy of <paramref name="text"/>, given the chunk's metadata for
    /// context. Returns the original text unchanged if there is nothing to redact.
    /// </summary>
    string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata);
}
