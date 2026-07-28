using System.Diagnostics;
using Microsoft.Extensions.AI;
using Rag.NET.Diagnostics.Internal;
using Xunit;

namespace Rag.NET.Diagnostics.Tests;

/// <summary>
/// The assembled prompt is the one part of a query nothing else can see, and the only part whose
/// capture required touching production code. These pin what that seam records.
/// </summary>
public sealed class TracePromptObserverTests
{
    [Fact]
    public void TheAssembledPrompt_IsRecordedUnderTheCurrentTraceIdWithItsRoles()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CapturePromptText = true }, buffer);
        var observer = new TracePromptObserver(collector);

        using var activity = StartActivity();

        observer.OnPromptAssembled(
        [
            new ChatMessage(ChatRole.System, "answer from the context"),
            new ChatMessage(ChatRole.User, "Context:\nchunk one\n\nQuestion: who approved this"),
        ]);

        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);

        // Roles are kept: whether a hardening prefix landed ahead of the system prompt, or the
        // history arrived at all, is exactly what someone opens a trace to check.
        Assert.Equal(
            "system: answer from the context\n\nuser: Context:\nchunk one\n\nQuestion: who approved this",
            trace.Prompt);
    }

    [Fact]
    public void WithDefaultOptions_ThePromptIsNotRetained()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions(), buffer);
        var observer = new TracePromptObserver(collector);

        using var activity = StartActivity();

        observer.OnPromptAssembled([new ChatMessage(ChatRole.User, "what is the admin password")]);

        var trace = collector.Current(activity.TraceId.ToHexString());
        Assert.NotNull(trace);

        // The prompt holds the question and the documents together, so it is the single field most
        // worth withholding by default.
        Assert.Null(trace.Prompt);
    }

    [Fact]
    public void WithNoAmbientActivity_NothingIsRecordedAndNothingThrows()
    {
        var buffer = new TraceRingBuffer(capacity: 10);
        var collector = new TraceCollector(new RagTraceOptions { CapturePromptText = true }, buffer);
        var observer = new TracePromptObserver(collector);

        Assert.Null(Activity.Current);

        observer.OnPromptAssembled([new ChatMessage(ChatRole.User, "q")]);

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void WhenCaptureThrows_TheObserverSwallowsIt()
    {
        var observer = new TracePromptObserver(new ThrowingTraceCollector());

        using var activity = StartActivity();

        // IPromptObserver's contract is that implementations never throw: this is called on the path
        // to the model, so an escaping exception would turn a diagnostic into a failed answer.
        observer.OnPromptAssembled([new ChatMessage(ChatRole.User, "q")]);
    }

    /// <summary>Starts a W3C activity so <c>Activity.Current</c> has a trace id to join on.</summary>
    /// <returns>The started activity; dispose it to restore the previous ambient activity.</returns>
    private static Activity StartActivity()
    {
        var activity = new Activity("test");
        activity.Start();

        return activity;
    }
}
