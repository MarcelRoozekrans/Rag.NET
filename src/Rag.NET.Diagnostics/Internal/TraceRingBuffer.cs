using System.Diagnostics.CodeAnalysis;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Holds the most recent <c>capacity</c> traces and drops the rest. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The bound is the point: a debugger that grows with traffic is a memory leak with a nice name.
/// Once full, adding evicts the oldest — a trace is read minutes after the request that produced it,
/// so the newest are the ones worth keeping and <see cref="Snapshot"/> returns them newest first.
/// </para>
/// <para>
/// A lock around a fixed array, deliberately, rather than a lock-free ring. The critical section is
/// one array write and one index increment; a CAS loop here would trade obvious correctness for
/// nothing measurable, on a path that only runs when someone has turned diagnostics on.
/// </para>
/// </remarks>
internal sealed class TraceRingBuffer
{
    private readonly Lock _gate = new();
    private readonly RagTrace?[] _slots;

    /// <summary>Where the next trace is written; one past the newest.</summary>
    private int _next;

    /// <summary>How many slots hold a trace. Stops rising at the capacity.</summary>
    private int _count;

    /// <summary>Creates an empty buffer.</summary>
    /// <param name="capacity">The most traces to keep. Must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is below 1.</exception>
    public TraceRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _slots = new RagTrace?[capacity];
    }

    /// <summary>Records a trace, evicting the oldest if the buffer is already full.</summary>
    /// <param name="trace">The trace to keep.</param>
    public void Add(RagTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        lock (_gate)
        {
            _slots[_next] = trace;
            _next = (_next + 1) % _slots.Length;

            if (_count < _slots.Length)
                _count++;
        }
    }

    /// <summary>Copies out the retained traces, newest first.</summary>
    /// <returns>
    /// A point-in-time copy. A reader iterating it will not see writes that happened afterwards,
    /// which is what makes it safe to walk a trace list while the pipeline keeps serving.
    /// </returns>
    public IReadOnlyList<RagTrace> Snapshot()
    {
        lock (_gate)
        {
            var copy = new RagTrace[_count];

            for (var i = 0; i < _count; i++)
                copy[i] = _slots[SlotOfNewest(i)]!;

            return copy;
        }
    }

    /// <summary>Finds a retained trace by its id.</summary>
    /// <param name="traceId">The id to look for.</param>
    /// <param name="trace">The trace, when one is still retained.</param>
    /// <returns>
    /// <see langword="false"/> when no such trace is held — including when it was held and has since
    /// been evicted, which the caller cannot distinguish and does not need to.
    /// </returns>
    public bool TryGet(string traceId, [NotNullWhen(true)] out RagTrace? trace)
    {
        ArgumentNullException.ThrowIfNull(traceId);

        lock (_gate)
        {
            // Newest first, so a repeated id — which the pipeline should not produce, but nothing
            // here enforces — resolves to the most recent execution rather than a stale one.
            for (var i = 0; i < _count; i++)
            {
                var candidate = _slots[SlotOfNewest(i)];

                if (candidate is not null && string.Equals(candidate.TraceId, traceId, StringComparison.Ordinal))
                {
                    trace = candidate;
                    return true;
                }
            }
        }

        trace = null;
        return false;
    }

    /// <summary>
    /// The slot holding the <paramref name="offset"/>-th newest trace. Call under the lock.
    /// </summary>
    /// <remarks>
    /// Walks backwards from <c>_next - 1</c>, the newest write. Adding the length once is enough to
    /// clear the negative range: <paramref name="offset"/> is below <c>_count</c>, which never
    /// exceeds the length, so the smallest value the expression can reach is exactly <c>-length</c>.
    /// </remarks>
    private int SlotOfNewest(int offset) => (_next - 1 - offset + _slots.Length) % _slots.Length;
}
