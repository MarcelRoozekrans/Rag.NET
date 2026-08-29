using Microsoft.Extensions.AI;
using Rag.NET.Benchmarks.Quality;

namespace Rag.NET.Benchmarks.Quality.GraphExtractions;

/// <summary>
/// The <see cref="IChatClient"/> GraphRAG's behaviors are handed, <b>whichever stage they belong
/// to</b>: every request is answered from <see cref="GraphExtractionCache"/>, and only a fill-mode
/// miss reaches the model underneath.
/// <para>
/// <b>Nothing here is specific to extraction, and the name used to say otherwise.</b> The class
/// takes a cache, an optional model and a temperature, and keys on the rendered prompt — so it
/// serves <c>GraphEntityExtractionBehavior</c> and <c>CommunityDetectionBehavior</c> equally, over
/// whichever directory of the cache the caller opened. It was called
/// <c>CachedGraphExtractionClient</c> until community reports were cached the same way; a name that
/// has stopped describing what the code does is the drift this repository renamed Leiden over.
/// </para>
/// <para>
/// <b>The decorator is where the cache meets the library, and it is deliberately the only place.</b>
/// The behaviors render their own prompts — the extraction template with the chunk text
/// substituted, the gleaning follow-up embedding the previous extraction as JSON, the report
/// template with a community's members and their merged descriptions substituted — and neither the
/// generation tool nor the guard is allowed to render them itself. Intercepting at the client means
/// whatever the behavior asks is exactly what the key is computed from, and a change to a template,
/// to the configured type constraints or to the report prompt's length bound misses rather than
/// silently reusing text produced under different instructions.
/// </para>
/// </summary>
/// <remarks>
/// Streaming throws rather than falling back. Nothing in GraphRAG streams, and a decorator that
/// quietly passed a streaming call through to the model would be an uncached, unrecorded LLM call
/// inside a run whose whole claim is that it makes none.
/// </remarks>
public sealed class CachedGraphRagClient : IChatClient
{
    /// <summary>How many times one request is attempted before its failure propagates.</summary>
    private const int MaxAttempts = 5;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);

    private readonly GraphExtractionCache _cache;
    private readonly IChatClient? _inner;
    private readonly ChatOptions _options;
    private long _calls;
    private long _longestPrompt;
    private long _retries;

    // Token usage, accumulated across every LIVE call. Cache hits contribute nothing, which is
    // the point: these figures are what the run actually paid for, not what it would have paid
    // with a cold cache. Issue #200.
    private long _inputTokens;
    private long _outputTokens;
    private long _totalTokens;
    private long _callsWithUsage;
    private long _callsWithoutUsage;

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
    public CachedGraphRagClient(
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

    /// <summary>
    /// Gets the character length of the longest prompt this client was ever handed, summed over the
    /// request's messages.
    /// </summary>
    /// <remarks>
    /// <b>Measured on every run rather than quoted from one, and it lives here now because this is
    /// where the report prompts pass.</b> It was <c>PromptEchoChatClient.LongestPrompt</c> while
    /// the guard synthesised its reports; a figure that moved with the code that measures it would
    /// have been dropped in the move, and this is the single most consequential number about the
    /// community stage. The report prompt had no bound at all until
    /// <c>GraphRagOptions.MaxCommunityReportPromptLength</c> was added: over sixty articles it
    /// reached 1,806,352 characters, some 450,000 tokens against gpt-4o-mini's 128,000-token
    /// context. An earlier note put it at 976,425, which was the entity block alone and about half
    /// of what is actually sent — a number nobody re-measures drifts, so this one is re-measured.
    /// <para>
    /// Summed over every message rather than taken from the first: the report prompt is one message
    /// today, but a behavior that split its instructions from its entity block would otherwise
    /// report half of what it sends.
    /// </para>
    /// </remarks>
    public long LongestPrompt => Interlocked.Read(ref _longestPrompt);

    /// <summary>
    /// Gets how many model attempts failed and were retried — rate-limit responses, transient
    /// errors and blank replies alike.
    /// </summary>
    /// <remarks>
    /// The figure that says whether a concurrency bound is inside the provider's tolerance:
    /// <c>GraphRagOptions.CommunityReportConcurrency</c> widens the report stage, and a bound that
    /// finishes faster while this climbs is trading wall clock for retried requests rather than
    /// saving anything. Counted here rather than parsed out of the console, so the tool can print
    /// it beside the rate it measured.
    /// </remarks>
    public long Retries => Interlocked.Read(ref _retries);

    /// <summary>Input (prompt) tokens billed across every live call this run made.</summary>
    public long InputTokens => Interlocked.Read(ref _inputTokens);

    /// <summary>Output (completion) tokens billed across every live call this run made.</summary>
    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    /// <summary>Total tokens as the provider reported them.</summary>
    /// <remarks>
    /// Reported rather than derived: a provider's total is not always input + output — reasoning
    /// and cached-prompt tokens are billed separately by some models — so adding the two halves
    /// would quietly invent a number. Where they disagree, the provider's total is the one that
    /// matches the invoice.
    /// </remarks>
    public long TotalTokens => Interlocked.Read(ref _totalTokens);

    /// <summary>Live calls that carried a usage figure.</summary>
    public long CallsWithUsage => Interlocked.Read(ref _callsWithUsage);

    /// <summary>
    /// Live calls that returned <b>no</b> usage at all, so are absent from the totals above.
    /// </summary>
    /// <remarks>
    /// Counted separately rather than treated as zero. <c>ChatResponse.Usage</c> is optional in
    /// <c>Microsoft.Extensions.AI</c> and a provider may omit it — silently folding those into the
    /// totals would understate the cost by exactly the amount nobody can see, which is the failure
    /// mode issue #200 exists to end. A non-zero count here means the reported spend is a floor.
    /// </remarks>
    public long CallsWithoutUsage => Interlocked.Read(ref _callsWithoutUsage);

    /// <summary>One line describing what this run's live calls cost, in tokens.</summary>
    public string DescribeUsage()
    {
        var withUsage = CallsWithUsage;
        var withoutUsage = CallsWithoutUsage;
        if (withUsage == 0 && withoutUsage == 0)
        {
            return "Tokens: no live calls — everything replayed from cache, so this run cost nothing.";
        }

        var line = FormattableString.Invariant(
            $"Tokens: {InputTokens:N0} in + {OutputTokens:N0} out = {TotalTokens:N0} total, over {withUsage:N0} live call(s) that reported usage.");

        if (withoutUsage == 0)
        {
            return line;
        }

        var caveat = FormattableString.Invariant(
            $" WARNING: {withoutUsage:N0} live call(s) reported no usage and are NOT in these totals, so treat the figures as a floor rather than the cost.");

        return line + caveat;
    }

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
        RecordPromptLength(sent);

        var merged = Merge(options);
        var text = await _cache.GetOrAddAsync(
            GraphExtractionPrompt.Render(sent),
            ct => CallModelAsync(sent, merged, ct),
            RenderOptionsKey(options),
            cancellationToken);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "The cached GraphRAG client does not stream. Nothing in GraphRAG streams today, and " +
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

    /// <summary>Keeps the high-water mark of how much text one request carried.</summary>
    /// <remarks>
    /// Recorded on the way in, before the cache is consulted, so a replay run measures the prompts
    /// the pipeline built rather than only the ones it had to generate. The high-water mark is
    /// updated with a compare-exchange loop because the generation tool runs articles concurrently.
    /// </remarks>
    private void RecordPromptLength(List<ChatMessage> messages)
    {
        long total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            total += messages[i].Text.Length;
        }

        var seen = Interlocked.Read(ref _longestPrompt);
        while (total > seen)
        {
            var previous = Interlocked.CompareExchange(ref _longestPrompt, total, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
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
        List<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
    {
        if (_inner is null)
        {
            throw new InvalidOperationException(
                "The cached GraphRAG client was constructed without a model, so a cache miss " +
                "cannot be filled. A refuse-on-miss cache should have thrown before reaching here; " +
                "a fill-mode cache was opened without a client to fill it from.");
        }

        var delay = FirstRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CallOnceAsync(messages, options, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                _ = Interlocked.Increment(ref _retries);
                await Console.Error.WriteLineAsync(FormattableString.Invariant(
                    $"  attempt {attempt}/{MaxAttempts} failed, retrying in {delay.TotalSeconds:F0}s: {ex.Message}"));
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    /// <summary>One attempt, refusing blank text.</summary>
    private async Task<string> CallOnceAsync(
        List<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken)
    {
        var response = await _inner!.GetResponseAsync(messages, options, cancellationToken);
        RecordUsage(response);
        return string.IsNullOrWhiteSpace(response.Text)
            ? throw new InvalidOperationException(
                "The model returned blank text; retrying rather than caching it.")
            : response.Text;
    }

    /// <summary>
    /// The caller's options over this client's baseline: the baseline's temperature stays
    /// authoritative because the model identity carries it into every cache key.
    /// </summary>
    private ChatOptions Merge(ChatOptions? callerOptions)
    {
        if (callerOptions is null)
        {
            return _options;
        }

        var merged = callerOptions.Clone();
        merged.Temperature = _options.Temperature;
        return merged;
    }

    /// <summary>
    /// What the caller constrained beyond the baseline, canonically rendered, or an empty string
    /// when it constrained nothing.
    /// </summary>
    /// <remarks>
    /// Empty is the faithful encoding of every entry written before this existed, which is what lets
    /// all 86,510 of them keep their keys. <b>Exactly three fields are rendered</b> —
    /// <see cref="ChatOptions.MaxOutputTokens"/>, <see cref="ChatOptions.TopP"/> and
    /// <see cref="ChatOptions.Seed"/> — in that fixed order, so the same request always renders the
    /// same string. <see cref="ChatOptions.Temperature"/> is the one other field <see cref="Merge"/>
    /// forwards and is deliberately never rendered here: the baseline overwrites it before the call
    /// is sent, so nothing the caller passes there ever reaches the model.
    /// <para>
    /// <b>Every other field throws instead of being silently forwarded unkeyed.</b> <see cref="Merge"/>
    /// sends the caller's <em>whole</em> <see cref="ChatOptions"/> to the model, and a field this
    /// method does not render — <see cref="ChatOptions.ResponseFormat"/>,
    /// <see cref="ChatOptions.StopSequences"/>, <see cref="ChatOptions.FrequencyPenalty"/>,
    /// <see cref="ChatOptions.PresencePenalty"/>, <see cref="ChatOptions.Tools"/>,
    /// <see cref="ChatOptions.TopK"/>, <see cref="ChatOptions.Reasoning"/>,
    /// <see cref="ChatOptions.ModelId"/>, <see cref="ChatOptions.ConversationId"/>,
    /// <see cref="ChatOptions.ToolMode"/>, <see cref="ChatOptions.AllowMultipleToolCalls"/>,
    /// <see cref="ChatOptions.AllowBackgroundResponses"/>, <see cref="ChatOptions.ContinuationToken"/>,
    /// <see cref="ChatOptions.RawRepresentationFactory"/> or
    /// <see cref="ChatOptions.AdditionalProperties"/> — can change the response text just as surely
    /// as the three that are rendered. Two materially different requests that both left one of these
    /// set and unkeyed would silently collide on one cache entry and serve the wrong answer, so
    /// <see cref="ThrowIfUnkeyable"/> refuses the request instead.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A field that can change the response text is set and this method has no rendering for it.
    /// </exception>
    private static string RenderOptionsKey(ChatOptions? callerOptions)
    {
        if (callerOptions is null)
        {
            return string.Empty;
        }

        ThrowIfUnkeyable(callerOptions);

        var parts = new List<string>(3);
        if (callerOptions.MaxOutputTokens is { } maxTokens)
        {
            parts.Add(FormattableString.Invariant($"maxOutputTokens={maxTokens}"));
        }

        if (callerOptions.TopP is { } topP)
        {
            parts.Add(FormattableString.Invariant($"topP={topP}"));
        }

        if (callerOptions.Seed is { } seed)
        {
            parts.Add(FormattableString.Invariant($"seed={seed}"));
        }

        return string.Join(";", parts);
    }

    /// <summary>
    /// Refuses a request that sets a response-affecting field <see cref="RenderOptionsKey"/> cannot
    /// render, rather than letting <see cref="Merge"/> forward it to the model unkeyed.
    /// </summary>
    /// <remarks>
    /// This is the repo's fail-loud posture applied to cache identity: an unkeyable request must
    /// fail rather than silently share a cache entry with a materially different one. Every field
    /// checked here is one <see cref="Merge"/> forwards verbatim from the caller's
    /// <see cref="ChatOptions"/> to the inner <see cref="IChatClient"/>.
    /// </remarks>
    private static void ThrowIfUnkeyable(ChatOptions options)
    {
        ThrowIfSet(options.Instructions is { Length: > 0 }, nameof(options.Instructions),
            "it is sent to the model as additional system-level guidance and can change the response text");
        ThrowIfSet(options.ResponseFormat is not null, nameof(options.ResponseFormat),
            "it constrains the shape of the model's reply (for example, forcing JSON)");
        ThrowIfSet(options.StopSequences is { Count: > 0 }, nameof(options.StopSequences),
            "it truncates the model's reply at a caller-chosen point");
        ThrowIfSet(options.FrequencyPenalty is not null, nameof(options.FrequencyPenalty),
            "it changes the sampled token distribution");
        ThrowIfSet(options.PresencePenalty is not null, nameof(options.PresencePenalty),
            "it changes the sampled token distribution");
        ThrowIfSet(options.Tools is { Count: > 0 }, nameof(options.Tools),
            "it can make the model reply with a tool call instead of text");
        ThrowIfSet(options.TopK is not null, nameof(options.TopK),
            "it changes the sampled token distribution");
        ThrowIfSet(options.Reasoning is not null, nameof(options.Reasoning),
            "it changes how much, and what kind of, reasoning the model does before replying");
        ThrowIfSet(options.ModelId is { Length: > 0 }, nameof(options.ModelId),
            "it can route the request to a different model than the one this client's identity names");
        ThrowIfSet(options.ConversationId is { Length: > 0 }, nameof(options.ConversationId),
            "it continues a specific server-side conversation, which the rendered messages alone do not capture");
        ThrowIfSet(options.ToolMode is not null, nameof(options.ToolMode),
            "it constrains whether and how the model must call a tool");
        ThrowIfSet(options.AllowMultipleToolCalls is not null, nameof(options.AllowMultipleToolCalls),
            "it changes whether the model may reply with more than one tool call");
        ThrowIfSet(options.AllowBackgroundResponses is not null, nameof(options.AllowBackgroundResponses),
            "it changes how the provider delivers the response");
        ThrowIfSet(options.ContinuationToken is not null, nameof(options.ContinuationToken),
            "it resumes a specific prior response rather than starting a new one");
        ThrowIfSet(options.RawRepresentationFactory is not null, nameof(options.RawRepresentationFactory),
            "it can rewrite the provider-specific request arbitrarily, which cannot be rendered into a key at all");
        ThrowIfSet(options.AdditionalProperties is { Count: > 0 }, nameof(options.AdditionalProperties),
            "it carries provider-specific settings this client has no way to enumerate or render");
    }

    /// <summary>Throws <see cref="Unkeyable"/> for <paramref name="field"/> when <paramref name="isSet"/>.</summary>
    private static void ThrowIfSet(bool isSet, string field, string reason)
    {
        if (isSet)
        {
            throw Unkeyable(field, reason);
        }
    }

    private static InvalidOperationException Unkeyable(string field, string reason) =>
        new(
            "ChatOptions." + field + " was set, but CachedGraphRagClient.RenderOptionsKey does not " +
            "render it into the cache key, and " + reason + ". Sending it to the model unkeyed risks " +
            "two materially different requests silently colliding on one cache entry and serving the " +
            "wrong answer. Only MaxOutputTokens, TopP and Seed are keyed today (Temperature is " +
            "baseline-authoritative and is overwritten before the call is sent) — extend " +
            "RenderOptionsKey to cover " + field + " before setting it on a call that reaches this " +
            "client.");

    /// <summary>
    /// Accumulates one live response's token usage. Called on every attempt, including retried
    /// ones, because a retried request is billed too.
    /// </summary>
    private void RecordUsage(ChatResponse response)
    {
        var usage = response.Usage;
        if (usage is null)
        {
            _ = Interlocked.Increment(ref _callsWithoutUsage);
            return;
        }

        _ = Interlocked.Increment(ref _callsWithUsage);
        _ = Interlocked.Add(ref _inputTokens, usage.InputTokenCount ?? 0);
        _ = Interlocked.Add(ref _outputTokens, usage.OutputTokenCount ?? 0);
        _ = Interlocked.Add(ref _totalTokens, usage.TotalTokenCount ?? 0);
    }
}
