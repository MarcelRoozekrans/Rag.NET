namespace Rag.NET.Pipeline;

/// <summary>
/// Wraps a delegate chain built once at startup.
/// ExecuteAsync has zero allocation on the hot path — all closures are captured at build time.
/// </summary>
public sealed class Pipeline<TContext, TResult>(
    Func<TContext, CancellationToken, ValueTask<TResult>> chain)
{
    public ValueTask<TResult> ExecuteAsync(TContext ctx, CancellationToken ct) => chain(ctx, ct);
}
