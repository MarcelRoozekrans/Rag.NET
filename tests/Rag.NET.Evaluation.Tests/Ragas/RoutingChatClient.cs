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
    private TaskCompletionSource? _release;
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
    /// Blocks every call until <see cref="ReleaseAll"/> is called, so a test can observe how many
    /// the judge let start at once.
    /// </summary>
    public void GateCalls()
        => _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Unblocks every call gated by <see cref="GateCalls"/>.</summary>
    public void ReleaseAll() => _release?.TrySetResult();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = JoinText(messages);

        lock (_gate)
        {
            _prompts.Add(text);
        }

        UpdatePeak(Interlocked.Increment(ref _inFlight));

        try
        {
            if (_release is not null)
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new ChatResponse([new ChatMessage(ChatRole.Assistant, Route(text))])
            {
                Usage = Usage,
            };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
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
        // Nothing to release: the gate is a TaskCompletionSource, not an OS handle.
    }
}
