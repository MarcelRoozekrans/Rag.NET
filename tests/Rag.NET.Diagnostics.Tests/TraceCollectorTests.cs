using Rag.NET.Diagnostics.Internal;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The collector is where content capture is decided, so this is where the phase's safety property is
/// pinned: with default options a trace holds structure and no text at all.
/// </summary>
public sealed class TraceCollectorTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";

    [Fact]
    public void WithDefaultOptions_NoTextIsCaptured()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordQuery(TraceId, "what is the admin password");
        collector.RecordChunks(TraceId, [ChunkWithText("secret contract text")]);
        collector.RecordPrompt(TraceId, "system: ...\nuser: what is the admin password");
        collector.RecordAnswer(TraceId, "the password is hunter2");
        collector.Commit(TraceId);

        var trace = Assert.Single(buffer.Snapshot());

        // Defaults must be safe. "Turn on debugging" must not silently mean "start retaining
        // customer documents and user questions in memory". Flipping any one of the four Capture*
        // defaults has to break this test — that is checked by mutation, not by reading the code.
        Assert.Null(trace.Query);
        Assert.Null(trace.Prompt);
        Assert.Null(trace.Answer);

        var chunk = Assert.Single(trace.Chunks);
        Assert.Null(chunk.Text);

        // Structure is still captured — that is the point of the default.
        Assert.NotEmpty(trace.QueryHash);
        Assert.Equal("doc-1", chunk.DocumentId);
        Assert.Equal(0.9, chunk.Score);
    }

    [Fact]
    public void WithEveryFlagOn_TextIsCapturedVerbatim()
    {
        var options = new RagTraceOptions
        {
            CaptureQueryText = true,
            CaptureChunkText = true,
            CapturePromptText = true,
            CaptureAnswerText = true,
        };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);

        collector.RecordQuery(TraceId, "the question");
        collector.RecordChunks(TraceId, [ChunkWithText("the chunk")]);
        collector.RecordPrompt(TraceId, "the prompt");
        collector.RecordAnswer(TraceId, "the answer");
        collector.Commit(TraceId);

        var trace = Assert.Single(buffer.Snapshot());

        Assert.Equal("the question", trace.Query);
        Assert.Equal("the chunk", Assert.Single(trace.Chunks).Text);
        Assert.Equal("the prompt", trace.Prompt);
        Assert.Equal("the answer", trace.Answer);
    }

    [Fact]
    public void EachFlagGatesOnlyItsOwnField()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CaptureQueryText = true }, buffer);

        collector.RecordQuery(TraceId, "the question");
        collector.RecordChunks(TraceId, [ChunkWithText("the chunk")]);
        collector.RecordPrompt(TraceId, "the prompt");
        collector.RecordAnswer(TraceId, "the answer");
        collector.Commit(TraceId);

        var trace = Assert.Single(buffer.Snapshot());

        // One gate, but four flags: turning one on must not turn the others on with it.
        Assert.Equal("the question", trace.Query);
        Assert.Null(Assert.Single(trace.Chunks).Text);
        Assert.Null(trace.Prompt);
        Assert.Null(trace.Answer);
    }

    [Fact]
    public void OverTheCharacterCap_TextIsCutAndTheCutIsVisible()
    {
        var options = new RagTraceOptions
        {
            CaptureQueryText = true,
            CaptureChunkText = true,
            CapturePromptText = true,
            CaptureAnswerText = true,
            MaxCapturedCharacters = 10,
        };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);

        collector.RecordQuery(TraceId, new string('q', 50));
        collector.RecordChunks(TraceId, [ChunkWithText(new string('c', 50))]);
        collector.RecordPrompt(TraceId, new string('p', 50));
        collector.RecordAnswer(TraceId, new string('a', 50));
        collector.Commit(TraceId);

        var trace = Assert.Single(buffer.Snapshot());

        // Visible, not silent: a trace that looked complete but had been cut would mislead exactly
        // when someone is reading it to find out where the prompt went wrong.
        Assert.Equal(new string('q', 10) + RagTraceOptions.TruncationMarker, trace.Query);
        Assert.Equal(new string('c', 10) + RagTraceOptions.TruncationMarker, Assert.Single(trace.Chunks).Text);
        Assert.Equal(new string('p', 10) + RagTraceOptions.TruncationMarker, trace.Prompt);
        Assert.Equal(new string('a', 10) + RagTraceOptions.TruncationMarker, trace.Answer);
    }

    [Fact]
    public void ExactlyAtTheCharacterCap_TextIsNotMarkedAsCut()
    {
        var options = new RagTraceOptions { CaptureQueryText = true, MaxCapturedCharacters = 10 };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);

        collector.RecordQuery(TraceId, new string('q', 10));
        collector.Commit(TraceId);

        // Marking a value that was kept whole would be as misleading as not marking one that was cut.
        Assert.Equal(new string('q', 10), Assert.Single(buffer.Snapshot()).Query);
    }

    [Fact]
    public void WithACapOfZero_TheFieldIsTheMarkerAlone_NotNull()
    {
        var options = new RagTraceOptions { CaptureQueryText = true, MaxCapturedCharacters = 0 };
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(options, buffer);

        collector.RecordQuery(TraceId, "something was asked");
        collector.Commit(TraceId);

        // "There was text and none of it was kept" is a different fact from "the flag was off",
        // which is what null means. Confirming the wiring must not look like capture being disabled.
        Assert.Equal(RagTraceOptions.TruncationMarker, Assert.Single(buffer.Snapshot()).Query);
    }

    [Fact]
    public void GuardActionText_IsGatedByTheChunkTextFlag()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordGuardAction(TraceId, GuardAction(), TraceContentKind.Chunk);
        collector.Commit(TraceId);

        var action = Assert.Single(Assert.Single(buffer.Snapshot()).GuardActions);

        // The counts are the diagnostic; the text is the content. Only the second is gated.
        Assert.Null(action.InputText);
        Assert.Null(action.OutputText);
        Assert.Equal(5, action.InputCount);
        Assert.Equal(3, action.OutputCount);
        Assert.True(action.Changed);
    }

    [Fact]
    public void GuardActionText_IsCapturedWhenTheChunkTextFlagIsOn()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CaptureChunkText = true }, buffer);

        collector.RecordGuardAction(TraceId, GuardAction(), TraceContentKind.Chunk);
        collector.Commit(TraceId);

        var action = Assert.Single(Assert.Single(buffer.Snapshot()).GuardActions);

        Assert.Equal("five results", action.InputText);
        Assert.Equal("three results", action.OutputText);
    }

    [Fact]
    public void AQueryKindGuardAction_IsGatedByTheQueryFlagAndNotTheChunkFlag()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CaptureChunkText = true }, buffer);

        collector.RecordGuardAction(TraceId, GuardAction(), TraceContentKind.Query);
        collector.Commit(TraceId);

        var action = Assert.Single(Assert.Single(buffer.Snapshot()).GuardActions);

        // A query sanitiser's text IS the user's question. Gating every guard action on the chunk
        // flag — which this collector used to do — meant "capture document text" silently also meant
        // "retain everything anybody asked", with CaptureQueryText still off.
        Assert.Null(action.InputText);
        Assert.Null(action.OutputText);
        Assert.Equal(5, action.InputCount);
    }

    [Fact]
    public void AQueryKindGuardAction_IsCapturedWhenTheQueryFlagIsOn()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CaptureQueryText = true }, buffer);

        collector.RecordGuardAction(TraceId, GuardAction(), TraceContentKind.Query);
        collector.Commit(TraceId);

        var action = Assert.Single(Assert.Single(buffer.Snapshot()).GuardActions);

        Assert.Equal("five results", action.InputText);
        Assert.Equal("three results", action.OutputText);
    }

    [Fact]
    public void AnUnrecognisedContentKind_CapturesNothingEvenWithEveryFlagOn()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var options = new RagTraceOptions
        {
            CaptureQueryText = true,
            CaptureChunkText = true,
            CapturePromptText = true,
            CaptureAnswerText = true,
        };
        var collector = new TraceCollector(options, buffer);

        // A kind no flag governs, which is what a future TraceContentKind added without a matching
        // option looks like from inside the content gate. Every flag is on, so the only thing that
        // can withhold the text is the gate's default arm failing closed — and the whole point of
        // that arm is the case where retaining text nobody asked to retain is the failure mode.
        collector.RecordGuardAction(TraceId, GuardAction(), (TraceContentKind)999);
        collector.Commit(TraceId);

        var action = Assert.Single(Assert.Single(buffer.Snapshot()).GuardActions);

        Assert.Null(action.InputText);
        Assert.Null(action.OutputText);

        // The action itself is still recorded: failing closed withholds the content, not the fact
        // that a guard fired. Without this the assertions above would also pass if nothing at all
        // had been recorded.
        Assert.Equal(5, action.InputCount);
        Assert.Equal(3, action.OutputCount);
    }

    [Fact]
    public void QueryHash_IsStableForTheSameQueryAndDiffersForAnother()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordQuery("a", "the same question");
        collector.Commit("a");
        collector.RecordQuery("b", "the same question");
        collector.Commit("b");
        collector.RecordQuery("c", "a different question");
        collector.Commit("c");

        var byId = buffer.Snapshot().ToDictionary(t => t.TraceId, t => t.QueryHash, StringComparer.Ordinal);

        // Without this the hash cannot do the one job it is kept for: telling repeated questions
        // apart while retaining none of them.
        Assert.Equal(byId["a"], byId["b"]);
        Assert.NotEqual(byId["a"], byId["c"], StringComparer.Ordinal);
    }

    [Fact]
    public void ChunksRecordedTwice_AreAppendedRatherThanReplaced()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordChunks(TraceId, [ChunkWithText("first", index: 0)]);
        collector.RecordChunks(TraceId, [ChunkWithText("second", index: 1)]);
        collector.Commit(TraceId);

        // A pipeline can retrieve more than once. "Which chunks came back" must not quietly become
        // "the chunks from the last retrieval".
        var trace = Assert.Single(buffer.Snapshot());
        int[] expected = [0, 1];
        Assert.Equal(expected, trace.Chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public void AnUncommittedTrace_IsReadableButNotInTheBuffer()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordChunks(TraceId, [ChunkWithText("in progress")]);

        Assert.Empty(buffer.Snapshot());
        Assert.Single(collector.Current(TraceId)!.Chunks);
    }

    [Fact]
    public void AfterCommit_TheTraceIsNoLongerInFlight()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordQuery(TraceId, "q");
        collector.Commit(TraceId);

        // Committed traces are read from the buffer. Leaving a copy in flight would both leak and
        // let a second Commit publish the same execution twice.
        Assert.Null(collector.Current(TraceId));
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public void CommittingAnIdNothingWasRecordedUnder_DoesNothing()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.Commit("never-seen");

        // The normal outcome when a request failed before reaching anything that records.
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Stages_AreRecordedAsGiven()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordStage(TraceId, new TraceStage
        {
            Name = "ragnet.retrieve",
            StartedAt = DateTimeOffset.UnixEpoch,
            Duration = TimeSpan.FromMilliseconds(12),
        });
        collector.Commit(TraceId);

        var stage = Assert.Single(Assert.Single(buffer.Snapshot()).Stages);
        Assert.Equal("ragnet.retrieve", stage.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(12), stage.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RecordingUnderAnAbsentTraceId_IsIgnoredRatherThanThrowing(string? traceId)
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        // A pipeline running without an ActivityListener has no trace id. That is normal, and it
        // must be a silent no-op rather than a crash in the middle of somebody's query.
        collector.RecordQuery(traceId!, "q");
        collector.Commit(traceId!);

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void RecordingNullPayloads_IsIgnoredRatherThanThrowing()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);

        collector.RecordQuery(TraceId, null!);
        collector.RecordChunks(TraceId, null!);
        collector.RecordChunks(TraceId, [null!]);
        collector.RecordGuardAction(TraceId, null!, TraceContentKind.Chunk);
        collector.RecordStage(TraceId, null!);
        collector.RecordPrompt(TraceId, null!);
        collector.RecordAnswer(TraceId, null!);
        collector.Commit(TraceId);

        // A debugger that breaks the thing it observes is worse than no debugger, so nothing on the
        // recording path throws — the trace is left incomplete instead.
        var trace = Assert.Single(buffer.Snapshot());
        Assert.Empty(trace.Chunks);
        Assert.Empty(trace.GuardActions);
        Assert.Empty(trace.Stages);
    }

    [Fact]
    public void InFlightTraces_AreBoundedSoUncommittedOnesCannotAccumulate()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { Capacity = 1 }, buffer);

        // Capacity 1 allows four uncommitted traces; the fifth is refused. Without the ceiling a
        // pipeline that never commits would leak — the ring buffer's own bound, defeated one level up.
        for (var i = 0; i < 5; i++)
            collector.RecordQuery($"trace-{i}", "q");

        Assert.NotNull(collector.Current("trace-3"));
        Assert.Null(collector.Current("trace-4"));
    }

    [Fact]
    public async Task ConcurrentRecordingUnderOneTraceId_LosesNothing()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(
            () =>
            {
                for (var i = 0; i < 100; i++)
                    collector.RecordChunks(TraceId, [ChunkWithText("text", index: i)]);
            },
            cancellationToken)));

        collector.Commit(TraceId);

        // The parts of a trace arrive from different threads: a stage span stops wherever the stage
        // ran, the answer decorator continues elsewhere.
        Assert.Equal(800, Assert.Single(buffer.Snapshot()).Chunks.Count);
    }

    private static TraceChunk ChunkWithText(string text, int index = 0) => new()
    {
        DocumentId = "doc-1",
        ChunkIndex = index,
        Score = 0.9,
        Text = text,
    };

    private static TraceGuardAction GuardAction() => new()
    {
        Component = "RbacRetrievalGuard",
        InputCount = 5,
        OutputCount = 3,
        Changed = true,
        InputText = "five results",
        OutputText = "three results",
    };
}
