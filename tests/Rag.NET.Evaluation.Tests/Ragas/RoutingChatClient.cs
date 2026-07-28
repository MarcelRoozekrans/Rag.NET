using System.Text;
using Microsoft.Extensions.AI;

namespace Rag.NET.Evaluation.Tests.Ragas;

/// <summary>
/// An <see cref="IChatClient"/> that answers based on what the prompt contains, and records how
/// the calls actually interleaved.
/// </summary>
/// <remarks>
/// Hand-written rather than NSubstitute because RAGAS evaluators make sequenced,
/// prompt-dependent calls — extract a list, then judge each item — and a single canned reply
/// cannot express that. Peak concurrency is tracked because asserting a total call count proves
/// nothing about whether a ceiling held.
/// </remarks>
internal sealed class RoutingChatClient : IChatClient
{
    private readonly IReadOnlyList<(string Contains, string Reply)> _routes;
    private readonly string _fallback;
    private readonly Lock _gate = new();
    private readonly List<string> _prompts = [];
    private readonly List<(TaskCompletionSource Release, TaskCompletionSource Finished)> _gatedCalls = [];
    private bool _gateCalls;
    private int _inFlight;
    private int _peakInFlight;

    /// <summary>Creates a client that replies with the first route whose token the prompt contains.</summary>
    /// <param name="routes">Prompt substring to reply, tried in order.</param>
    /// <param name="fallback">Reply used when no route matches.</param>
    public RoutingChatClient(
        IReadOnlyList<(string Contains, string Reply)> routes,
        string fallback = "no")
    {
        _routes = routes;
        _fallback = fallback;
    }

    /// <summary>Every prompt seen, in the order the calls started.</summary>
    public IReadOnlyList<string> Prompts
    {
        get
        {
            lock (_gate)
            {
                return [.. _prompts];
            }
        }
    }

    /// <summary>The largest number of calls that were ever simultaneously in flight.</summary>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>How many calls have started.</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _prompts.Count;
            }
        }
    }

    /// <summary>Usage reported on each response. Null means the model reported none.</summary>
    public UsageDetails? Usage { get; set; }

    /// <summary>
    /// Blocks every call on its own latch until it is released, so a test can observe how many the
    /// judge let start at once, and in what order it let them finish.
    /// </summary>
    /// <remarks>
    /// Per call rather than one shared latch because completion order is itself under test: a
    /// judge that collected results in completion order rather than input order would be
    /// indistinguishable from a correct one if every call could only finish together.
    /// </remarks>
    public void GateCalls()
    {
        lock (_gate)
        {
            _gateCalls = true;
        }
    }

    /// <summary>Unblocks every call gated by <see cref="GateCalls"/>, and stops gating new ones.</summary>
    public void ReleaseAll()
    {
        foreach (var (release, _) in StopGating())
            release.TrySetResult();
    }

    /// <summary>
    /// Unblocks the gated calls last-started-first, waiting for each to produce its response
    /// before releasing the next, so completion order is the exact reverse of start order.
    /// </summary>
    public async Task ReleaseInReverseAsync()
    {
        var gated = StopGating();
        for (var i = gated.Count - 1; i >= 0; i--)
        {
            gated[i].Release.TrySetResult();
            await gated[i].Finished.Task.ConfigureAwait(false);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = JoinText(messages);
        TaskCompletionSource? release = null;
        TaskCompletionSource? finished = null;

        lock (_gate)
        {
            _prompts.Add(text);
            if (_gateCalls)
            {
                // Deliberately *without* RunContinuationsAsynchronously. Completing a latch then
                // runs the blocked call's continuation inline, all the way through to its verdict,
                // before TrySetResult returns — so the release order below is the completion
                // order, deterministically, rather than a race between two pool work items.
                release = new TaskCompletionSource();
                finished = new TaskCompletionSource();
                _gatedCalls.Add((release, finished));
            }
        }

        UpdatePeak(Interlocked.Increment(ref _inFlight));

        try
        {
            if (release is not null)
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new ChatResponse([new ChatMessage(ChatRole.Assistant, Route(text))])
            {
                Usage = Usage,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            finished?.TrySetResult();
        }
    }

    /// <summary>
    /// Stops gating further calls and returns the calls gated so far, in start order.
    /// </summary>
    /// <remarks>
    /// Both under the one lock: a call that gates between the snapshot and the flag being cleared
    /// would wait on a latch nobody holds a reference to any more, and hang the test run.
    /// </remarks>
    private List<(TaskCompletionSource Release, TaskCompletionSource Finished)> StopGating()
    {
        lock (_gate)
        {
            _gateCalls = false;
            return [.. _gatedCalls];
        }
    }

    private static string JoinText(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            if (builder.Length > 0)
                builder.Append('\n');

            builder.Append(message.Text);
        }

        return builder.ToString();
    }

    private string Route(string text)
    {
        foreach (var (contains, candidate) in _routes)
        {
            if (text.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return _fallback;
    }

    private void UpdatePeak(int observed)
    {
        var peak = Volatile.Read(ref _peakInFlight);
        while (observed > peak)
        {
            var prior = Interlocked.CompareExchange(ref _peakInFlight, observed, peak);
            if (prior == peak)
                return;

            peak = prior;
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("RAGAS does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to release: the latches are TaskCompletionSources, not OS handles.
    }
}
