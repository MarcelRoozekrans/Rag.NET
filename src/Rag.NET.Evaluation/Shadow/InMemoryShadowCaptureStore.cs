using System.Collections.Concurrent;

namespace Rag.NET.Evaluation.Shadow;

/// <summary>The trivial store: captured pairs in a list, in arrival order.</summary>
/// <remarks>
/// <b>For tests and samples, not production.</b> It exists so shadow mode works out of the box and
/// its tests need no infrastructure; it is unbounded, unencrypted, and gone with the process —
/// production captures contain verbatim user input and belong in a store the application chose
/// deliberately. See <see cref="IShadowCaptureStore"/> for what that choice entails.
/// </remarks>
public sealed class InMemoryShadowCaptureStore : IShadowCaptureStore
{
    private readonly ConcurrentQueue<ShadowCapture> _captures = new();

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before the write.</exception>
    public Task SaveAsync(ShadowCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();

        _captures.Enqueue(capture);
        return Task.CompletedTask;
    }

    /// <summary>A point-in-time copy of everything captured so far, in arrival order.</summary>
    /// <remarks>
    /// A copy rather than a live view: an offline comparison iterates it while the consumer keeps
    /// writing, and a live view would make that iteration race the traffic it is analysing.
    /// </remarks>
    public IReadOnlyList<ShadowCapture> Snapshot() => _captures.ToArray();
}
