namespace Rag.NET.Models.Options;

/// <summary>
/// Options for <c>UseRateLimiting</c>: per-minute request budgets per surface. A
/// <see langword="null"/> budget leaves that surface unlimited (and undecorated).
/// At least one budget must be set.
/// </summary>
public sealed class RateLimitingOptions
{
    /// <summary>
    /// Chat requests per minute (each streaming call counts once). Must be greater than
    /// zero when set; <see langword="null"/> (default) leaves chat calls unlimited.
    /// </summary>
    public int? ChatRequestsPerMinute { get; set; }

    /// <summary>
    /// Embedding requests per minute — counted per call, not per embedded value, since
    /// batching makes the call the natural unit. Must be greater than zero when set;
    /// <see langword="null"/> (default) leaves embedding calls unlimited.
    /// </summary>
    public int? EmbeddingRequestsPerMinute { get; set; }

    /// <summary>
    /// Maximum callers allowed to wait per surface; once full, further calls fail fast
    /// with <see cref="InvalidOperationException"/> instead of waiting. Must be greater
    /// than zero when set; <see langword="null"/> (default) means an unbounded queue.
    /// </summary>
    public int? MaxQueuedRequests { get; set; }
}
