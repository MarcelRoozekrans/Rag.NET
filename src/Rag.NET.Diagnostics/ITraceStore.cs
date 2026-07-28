using System.Diagnostics.CodeAnalysis;

namespace Rag.NET.Diagnostics;

/// <summary>Reads the traces capture has finished with.</summary>
/// <remarks>
/// <para>
/// The read half of the package, and the counterpart to <see cref="ITraceCollector"/>: the collector
/// assembles traces that are still running, this hands back the ones that are over.
/// <see cref="ITraceCollector.Current"/> is the only way to see a trace mid-flight and is not a
/// substitute — a trace disappears from it the moment it commits, which is exactly when a developer
/// wants to read it.
/// </para>
/// <para>
/// It exists as an interface rather than as the ring buffer itself because two callers need it and
/// neither should need the buffer: a developer reading a trace in-process, and
/// <c>Rag.NET.Diagnostics.AspNetCore</c>, which would otherwise have to be handed this package's
/// internals to serve one route.
/// </para>
/// <para>
/// Whatever is retained is bounded by <see cref="RagTraceOptions.Capacity"/>, so an id that was
/// readable a hundred queries ago may not be now. Traces are disposable by construction; the durable
/// record is <c>IAuditLog</c> in <c>Rag.NET.Security</c>.
/// </para>
/// </remarks>
public interface ITraceStore
{
    /// <summary>Copies out every retained trace, newest first.</summary>
    /// <returns>
    /// A point-in-time copy. A reader walking it will not observe traces committed afterwards, which
    /// is what makes it safe to read while the pipeline keeps serving.
    /// </returns>
    IReadOnlyList<RagTrace> Snapshot();

    /// <summary>Finds one retained trace by its id.</summary>
    /// <param name="traceId">The <c>Activity</c> trace id the execution ran under.</param>
    /// <param name="trace">The trace, when one is still retained.</param>
    /// <returns>
    /// <see langword="false"/> when no such trace is held — including when it was held and has since
    /// been evicted, which the caller cannot distinguish and does not need to. When an id was used
    /// twice, the newer trace wins.
    /// </returns>
    bool TryGet(string traceId, [NotNullWhen(true)] out RagTrace? trace);
}
