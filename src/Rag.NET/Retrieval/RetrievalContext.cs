using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Retrieval;

/// <summary>
/// Immutable per-call context for the retrieval pipeline.
/// Use <c>ctx with { ... }</c> to derive modified contexts in behaviors.
/// Services live on the behaviors, not here.
/// </summary>
public sealed record RetrievalContext
{
    // ── Runtime inputs ────────────────────────────────────────────────────
    public required string Query                   { get; init; }
    public required RetrievalOptions Options       { get; init; }

    // ── Logger — passed from facade for structured logging in behaviors ───
    public ILogger Logger                          { get; init; } = NullLogger.Instance;

    // ── Extension bag — custom behaviors store/read state here ───────────
    public Dictionary<string, object?> Extensions  { get; init; } = new();
}
