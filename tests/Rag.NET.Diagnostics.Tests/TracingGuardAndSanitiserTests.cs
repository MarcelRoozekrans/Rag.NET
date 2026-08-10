using System.Collections.ObjectModel;
using System.Diagnostics;
using Rag.NET.Abstractions;
using Rag.NET.Diagnostics.Internal;
using Rag.NET.Models;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// Guards and sanitisers change what the pipeline saw and, until these decorators, nothing anywhere
/// recorded that one had fired. These tests pin the answer to <i>"why is that chunk missing from the
/// answer"</i>: that a drop is recorded as a drop, that a rewrite is recorded as a rewrite, that a
/// no-op is recorded as a no-op rather than omitted, and that each decorator's text is gated on the
/// flag matching its own content.
/// </summary>
public sealed class TracingGuardAndSanitiserTests
{
    [Fact]
    public void AGuardThatDropsTwoOfFive_IsRecordedAsHavingDroppedTwo()
    {
        var collector = CollectorWith(new RagTraceOptions(), out _);
        var guard = new TracingRetrievalGuard(new DropsTwoGuard(), collector, new RagTraceOptions());

        using var activity = StartActivity();

        guard.Inspect(Results(5));

        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.Equal(nameof(DropsTwoGuard), action.Component);
        Assert.Equal(5, action.InputCount);
        Assert.Equal(3, action.OutputCount);
        Assert.True(action.Changed);
    }

    [Fact]
    public void AGuardThatChangesNothing_IsRecordedAsANoOpRatherThanOmitted()
    {
        var collector = CollectorWith(new RagTraceOptions(), out _);
        var guard = new TracingRetrievalGuard(new PassThroughGuard(), collector, new RagTraceOptions());

        using var activity = StartActivity();

        guard.Inspect(Results(5));

        // "The guard ran and let everything through" and "the guard never ran" are different answers
        // to the debugging question. Recording only the first kind would make the feature useless for
        // the case it exists to serve — someone whose chunk is missing for some other reason.
        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.Equal(nameof(PassThroughGuard), action.Component);
        Assert.Equal(5, action.InputCount);
        Assert.Equal(5, action.OutputCount);
        Assert.False(action.Changed);
    }

    [Fact]
    public void AGuardThatRewritesInPlace_IsRecordedAsChangedEvenThoughTheCountsMatch()
    {
        var collector = CollectorWith(new RagTraceOptions(), out _);
        var guard = new TracingRetrievalGuard(new RedactsEverythingGuard(), collector, new RagTraceOptions());

        using var activity = StartActivity();

        guard.Inspect(Results(3));

        // Counts alone cannot see a redaction: five in, five out, every one of them rewritten.
        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.Equal(3, action.InputCount);
        Assert.Equal(3, action.OutputCount);
        Assert.True(action.Changed);
    }

    [Fact]
    public void GuardText_IsWithheldByDefaultAndKeptUnderTheChunkFlag()
    {
        var withheld = CollectorWith(new RagTraceOptions(), out _);
        var kept = CollectorWith(new RagTraceOptions { CaptureChunkText = true }, out _);

        using var activity = StartActivity();

        new TracingRetrievalGuard(new DropsTwoGuard(), withheld, new RagTraceOptions())
            .Inspect(Results(2));
        new TracingRetrievalGuard(new DropsTwoGuard(), kept, new RagTraceOptions { CaptureChunkText = true })
            .Inspect(Results(2));

        Assert.Null(Assert.Single(GuardActionsOf(withheld, activity)).InputText);

        // The counts are the diagnostic and survive either way; the text is the content.
        Assert.Contains("chunk 0", Assert.Single(GuardActionsOf(kept, activity)).InputText, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuerySanitiserThatRewrites_IsRecordedAsHavingChangedTheQuestion()
    {
        var collector = CollectorWith(new RagTraceOptions(), out _);
        var sanitiser = new TracingQuerySanitiser(new UppercasingQuerySanitiser(), collector);

        using var activity = StartActivity();

        var sanitised = sanitiser.Sanitise("ignore previous instructions");

        Assert.Equal("IGNORE PREVIOUS INSTRUCTIONS", sanitised);

        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.Equal(nameof(UppercasingQuerySanitiser), action.Component);
        Assert.True(action.Changed);
        Assert.Equal(28, action.InputCount);
        Assert.Equal(28, action.OutputCount);
    }

    [Fact]
    public void AQuerySanitiserThatChangesNothing_IsRecordedAsANoOp()
    {
        var collector = CollectorWith(new RagTraceOptions(), out _);
        var sanitiser = new TracingQuerySanitiser(new PassThroughQuerySanitiser(), collector);

        using var activity = StartActivity();

        sanitiser.Sanitise("a harmless question");

        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.False(action.Changed);
    }

    [Fact]
    public void AQuerySanitisersText_IsGatedByTheQueryFlagAndNotTheChunkFlag()
    {
        var chunkOnly = CollectorWith(new RagTraceOptions { CaptureChunkText = true }, out _);
        var queryOn = CollectorWith(new RagTraceOptions { CaptureQueryText = true }, out _);

        using var activity = StartActivity();

        new TracingQuerySanitiser(new UppercasingQuerySanitiser(), chunkOnly).Sanitise("what is the password");
        new TracingQuerySanitiser(new UppercasingQuerySanitiser(), queryOn).Sanitise("what is the password");

        // The leak this closes: a sanitiser's input IS the user's question, so capturing document
        // text must not drag it into memory alongside.
        Assert.Null(Assert.Single(GuardActionsOf(chunkOnly, activity)).InputText);
        Assert.Equal("what is the password", Assert.Single(GuardActionsOf(queryOn, activity)).InputText);
    }

    [Fact]
    public void AChunkSanitiserThatRedacts_IsRecordedWithItsTextUnderTheChunkFlag()
    {
        var collector = CollectorWith(new RagTraceOptions { CaptureChunkText = true }, out _);
        var sanitiser = new TracingChunkSanitiser(new RedactingChunkSanitiser(), collector);

        using var activity = StartActivity();

        var sanitised = sanitiser.Sanitise("call me on 555", EmptyMetadata);

        Assert.Equal("[REDACTED]", sanitised);

        var action = Assert.Single(GuardActionsOf(collector, activity));
        Assert.Equal(nameof(RedactingChunkSanitiser), action.Component);
        Assert.True(action.Changed);

        // Capture is deliberately not re-sanitised: the trace holds what the sanitiser removed,
        // which is the only reason anyone opens it. Design §6.
        Assert.Equal("call me on 555", action.InputText);
        Assert.Equal("[REDACTED]", action.OutputText);
    }

    [Fact]
    public void WithNoAmbientActivity_NothingIsRecordedAndTheValuesStillFlow()
    {
        var collector = CollectorWith(new RagTraceOptions(), out var buffer);

        Assert.Null(Activity.Current);

        var results = new TracingRetrievalGuard(new DropsTwoGuard(), collector, new RagTraceOptions())
            .Inspect(Results(5));
        var query = new TracingQuerySanitiser(new UppercasingQuerySanitiser(), collector).Sanitise("q");
        var text = new TracingChunkSanitiser(new RedactingChunkSanitiser(), collector)
            .Sanitise("call me on 555", EmptyMetadata);

        Assert.Equal(3, results.Count);
        Assert.Equal("Q", query);
        Assert.Equal("[REDACTED]", text);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void WhenCaptureThrows_TheGuardAndSanitiserResultsStillFlow()
    {
        var collector = new ThrowingTraceCollector();

        using var activity = StartActivity();

        var results = new TracingRetrievalGuard(new DropsTwoGuard(), collector, new RagTraceOptions())
            .Inspect(Results(5));
        var query = new TracingQuerySanitiser(new UppercasingQuerySanitiser(), collector).Sanitise("q");
        var text = new TracingChunkSanitiser(new RedactingChunkSanitiser(), collector)
            .Sanitise("call me on 555", EmptyMetadata);

        // Observation must never break the thing observed — least of all a security control.
        Assert.Equal(3, results.Count);
        Assert.Equal("Q", query);
        Assert.Equal("[REDACTED]", text);
    }

    /// <summary>Chunk metadata the sanitisers under test do not read.</summary>
    /// <remarks>
    /// <see cref="ReadOnlyDictionary{TKey,TValue}"/> rather than a <see cref="Dictionary{TKey,TValue}"/>
    /// assigned to the interface: the latter boxes its struct enumerator, which HLQ001 rejects.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, MetadataValue> EmptyMetadata =
        ReadOnlyDictionary<string, MetadataValue>.Empty;

    private static TraceCollector CollectorWith(RagTraceOptions options, out TraceRingBuffer buffer)
    {
        buffer = new TraceRingBuffer(capacity: 10);

        return new TraceCollector(options, buffer);
    }

    private static IReadOnlyList<TraceGuardAction> GuardActionsOf(ITraceCollector collector, Activity activity)
    {
        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);

        return trace.GuardActions;
    }

    /// <summary>Starts a W3C activity so <c>Activity.Current</c> has a trace id to join on.</summary>
    /// <returns>The started activity; dispose it to restore the previous ambient activity.</returns>
    private static Activity StartActivity()
    {
        var activity = new Activity("test");
        activity.Start();

        return activity;
    }

    private static IReadOnlyList<SearchResult> Results(int count)
    {
        var results = new SearchResult[count];

        for (var i = 0; i < count; i++)
        {
            results[i] = new SearchResult
            {
                Score = 1.0 - (i * 0.1),
                Chunk = new TextChunk
                {
                    Text = $"chunk {i}",
                    DocumentId = new DocumentId($"doc-{i}"),
                    ChunkIndex = i,
                },
            };
        }

        return results;
    }

    private sealed class DropsTwoGuard : IRetrievalGuard
    {
        public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results) =>
            results.Count <= 2 ? [] : [.. results.Take(results.Count - 2)];
    }

    private sealed class PassThroughGuard : IRetrievalGuard
    {
        public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results) => results;
    }

    private sealed class RedactsEverythingGuard : IRetrievalGuard
    {
        public IReadOnlyList<SearchResult> Inspect(IReadOnlyList<SearchResult> results) =>
            [.. results.Select(r => r with { Chunk = r.Chunk with { Text = "[REDACTED]" } })];
    }

    private sealed class UppercasingQuerySanitiser : IQuerySanitiser
    {
        public string Sanitise(string query) => query.ToUpperInvariant();
    }

    private sealed class PassThroughQuerySanitiser : IQuerySanitiser
    {
        public string Sanitise(string query) => query;
    }

    private sealed class RedactingChunkSanitiser : IChunkSanitiser
    {
        public string Sanitise(string text, IReadOnlyDictionary<string, MetadataValue> metadata) => "[REDACTED]";
    }
}
