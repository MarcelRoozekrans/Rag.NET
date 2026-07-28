namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// An <see cref="ITraceCollector"/> whose every recording method throws.
/// </summary>
/// <remarks>
/// The real collector swallows its own failures, so a test using it can never demonstrate that the
/// behaviors and decorators around it swallow theirs. This one removes that safety net: anything
/// wired to it either holds the swallow-and-log line itself or fails the test by breaking the
/// pipeline, which is the whole property under examination.
/// </remarks>
internal sealed class ThrowingTraceCollector : ITraceCollector
{
    public void RecordQuery(string traceId, string query) => throw Failure();

    public void RecordChunks(string traceId, IReadOnlyList<TraceChunk> chunks) => throw Failure();

    public void RecordGuardAction(string traceId, TraceGuardAction action) => throw Failure();

    public void RecordStage(string traceId, TraceStage stage) => throw Failure();

    public void RecordPrompt(string traceId, string prompt) => throw Failure();

    public void RecordAnswer(string traceId, string answer) => throw Failure();

    public RagTrace? Current(string traceId) => throw Failure();

    public void Commit(string traceId) => throw Failure();

    private static InvalidOperationException Failure() =>
        new("A defect inside trace capture must never reach the pipeline.");
}
