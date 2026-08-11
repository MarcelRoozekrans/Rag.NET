namespace Rag.NET.Security;

/// <summary>
/// Configures which PII patterns <see cref="PiiChunkSanitiser"/> detects and redacts.
/// Pre-populated with <see cref="PiiPatterns.Defaults"/>. Add or remove entries to customise.
/// </summary>
public sealed class PiiDetectionOptions
{
    /// <summary>
    /// The active PII patterns. Patterns are compiled at <see cref="PiiChunkSanitiser"/> construction time.
    /// </summary>
    public IList<PiiPattern> Patterns { get; set; } = PiiPatterns.Defaults.ToList();
}
