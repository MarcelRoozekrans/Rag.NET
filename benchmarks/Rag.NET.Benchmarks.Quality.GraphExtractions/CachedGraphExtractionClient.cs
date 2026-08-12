using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The <see cref="IChatClient"/> GraphRAG's behaviors are handed: every request is answered from
/// <see cref="GraphExtractionCache"/>, and only a fill-mode miss reaches the model underneath.
/// <para>
/// <b>The decorator is where the cache meets the library, and it is deliberately the only place.</b>
/// <c>GraphEntityExtractionBehavior</c> renders its own prompts — the extraction template with the
/// chunk text substituted, then a gleaning follow-up embedding the previous extraction as JSON —
/// and neither the generation tool nor the guard is allowed to render them itself. Intercepting at
/// the client means whatever the behavior asks is exactly what the key is computed from, and a
/// change to the template or to the configured type constraints misses rather than silently
/// reusing an extraction made under different instructions.
/// </para>
/// </summary>
/// <remarks>
/// Streaming throws rather than falling back. Nothing in GraphRAG streams, and a decorator that
/// quietly passed a streaming call through to the model would be an uncached, unrecorded LLM call
/// inside a run whose whole claim is that it makes none.
/// </remarks>
public sealed class CachedGraphExtractionClient : IChatClient
{
    /// <summary>How many times one request is attempted before its failure propagates.</summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);

    private readonly GraphExtractionCache _cache;
    private readonly IChatClient? _inner;
    private readonly ChatOptions _options;
    private long _calls;

    /// <summary>Creates the decorator.</summary>
    /// <param name="cache">The cache every request is answered from.</param>
    /// <param name="inner">
    /// The model a fill-mode miss calls. <see langword="null"/> for a replay run, which cannot
    /// reach it: a refuse-on-miss cache throws before the delegate is invoked. Passing
    /// <see langword="null"/> rather than a client nobody intends to use is the difference between
    /// "there is no network here" and "there is one and we trust ourselves not to touch it".
    /// </param>
    /// <param name="temperature">
    /// The sampling temperature every request is sent with, normally
    /// <see cref="GraphExtractionModelIdentity.ExtractionTemperature"/>. It is not part of the
    /// prompt and therefore not part of the key — the model identity carries it, which is why the
    /// two must come from the same place.
    /// </param>
    public CachedGraphExtractionClient(
        GraphExtractionCache cache, IChatClient? inner, float temperature)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        _inner = inner;
        _options = new ChatOptions { Temperature = temperature };
    }

    /// <summary>Gets how many chat requests the behaviors have made through this client.</summary>
    /// <remarks>
    /// Requests, not model calls: a replay run's count is the number of prompts the pipeline
    /// produced, which is the figure that says whether the guard exercised the work the generation
    /// run paid for.
    /// </remarks>
    public long Calls => Interlocked.Read(ref _calls);

    /// <summary>Gets the cache behind this client.</summary>
    public GraphExtractionCache Cache => _cache;

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Materialised once: the sequence is walked twice — to compute the key, and to send — and
        // a caller's lazy enumerable could yield different messages the second time.
        var sent = new List<ChatMessage>(messages);
        _ = Interlocked.Increment(ref _calls);

        var text = await _cache.GetOrAddAsync(
            RenderPrompt(sent), ct => CallModelAsync(sent, ct), cancellationToken);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The cached extraction client does not stream. Nothing in GraphRAG streams today, and " +
            "passing a streaming call through would be an uncached, unrecorded model call inside a " +
            "run whose entire claim is that it makes none.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : _inner?.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() => _inner?.Dispose();

    /// <summary>
    /// Flattens a request into the one string the cache keys on: each message as
    /// <c>role: text</c>, newline separated.
    /// </summary>
    /// <remarks>
    /// GraphRAG sends exactly one user message today, so this is the prompt with six characters in
    /// front of it. The role prefix and the loop are there anyway because the alternative — take
    /// the single message's text — would key a two-message conversation as its first message the
    /// day a system prompt is added, and serve that day's extractions out of the day before's
    /// cache.
    /// </remarks>
    private static string RenderPrompt(List<ChatMessage> messages)
    {
        var rendered = new List<string>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            rendered.Add(messages[i].Role.ToString() + ": " + messages[i].Text);
        }

        return string.Join("\n", rendered);
    }

    /// <summary>
    /// Calls the model on a fill-mode miss, retrying transient failures with doubling delays.
    /// </summary>
    /// <remarks>
    /// Nothing is written on a failure path — <see cref="GraphExtractionCache.GetOrAddAsync"/>
    /// stores only what this returns — so a rate-limit page or an error string can never become a
    /// cached extraction. A blank response is treated as a failure and retried for the same reason:
    /// blank parses to an empty graph for that chunk, silently.
    /// </remarks>
    /// <exception cref="InvalidOperationException">There is no model to call.</exception>
    private async Task<string> CallModelAsync(
        List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_inner is null)
        {
            throw new InvalidOperationException(
                "The cached extraction client was constructed without a model, so a cache miss " +
                "cannot be filled. A refuse-on-miss cache should have thrown before reaching here; " +
                "a fill-mode cache was opened without a client to fill it from.");
        }

        var delay = FirstRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CallOnceAsync(messages, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                await Console.Error.WriteLineAsync(FormattableString.Invariant(
                    $"  attempt {attempt}/{MaxAttempts} failed, retrying in {delay.TotalSeconds:F0}s: {ex.Message}"));
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    /// <summary>One attempt, refusing blank text.</summary>
    private async Task<string> CallOnceAsync(
        List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var response = await _inner!.GetResponseAsync(messages, _options, cancellationToken);
        return string.IsNullOrWhiteSpace(response.Text)
            ? throw new InvalidOperationException(
                "The model returned blank text; retrying rather than caching it.")
            : response.Text;
    }
}
