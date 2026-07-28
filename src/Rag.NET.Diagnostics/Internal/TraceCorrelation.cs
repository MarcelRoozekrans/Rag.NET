using System.Diagnostics;

namespace Rag.NET.Diagnostics.Internal;

/// <summary>
/// Reads the key every part of a trace is joined on out of the ambient <see cref="Activity"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the one thing the diagnostics side does better than the audit side.
/// <c>AuditCorrelationContext</c> is a plain mutable field whose own XML has to warn that it must be
/// registered scoped or it will cross requests; <see cref="Activity.Current"/> is async-local, so it
/// is correct under concurrency with no registration advice attached to it at all.
/// </para>
/// <para>
/// <b>No ambient activity means no capture.</b> A pipeline running without an
/// <see cref="ActivityListener"/> subscribed produces no activities, and that is a normal way to run:
/// it must be a silent no-op. Fabricating an id instead would be worse than useless — every unjoined
/// fragment would become its own single-entry "trace" and fill the ring buffer with them.
/// </para>
/// </remarks>
internal static class TraceCorrelation
{
    /// <summary>The current trace id, as 32 lowercase hex characters.</summary>
    /// <returns>
    /// <see langword="null"/> when nothing is being traced, or when the ambient activity predates the
    /// W3C id format and therefore has no trace id to join on — a hierarchical activity's
    /// <see cref="Activity.TraceId"/> is all zeroes, which would silently merge every such execution
    /// into one trace.
    /// </returns>
    public static string? CurrentTraceId()
    {
        var activity = Activity.Current;

        return activity is null || activity.IdFormat != ActivityIdFormat.W3C
            ? null
            : activity.TraceId.ToHexString();
    }
}
