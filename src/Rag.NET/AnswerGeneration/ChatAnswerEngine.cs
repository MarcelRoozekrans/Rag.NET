using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.QueryTechniques.ContextualCompression;
using Rag.NET.Telemetry;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// Generates answers by building a context prompt from search results and calling an <see cref="IChatClient"/>.
/// </summary>
public sealed class ChatAnswerEngine(
    IChatClient chatClient,
    IConversationMemory? memory = null,
    IContextualCompressor? compressor = null,
    IPromptObserver? promptObserver = null,
    IGuidProvider? guidProvider = null) : IAnswerEngine
{
    private readonly IGuidProvider _guidProvider = guidProvider ?? SystemGuidProvider.Instance;

    /// <summary>The tool name the sources are attributed to when SendSourcesAsToolResult is set.</summary>
    private const string SourcesToolName = "SearchDocuments";

    private const string DefaultSystemPrompt =
        "Answer the user's question based only on the provided context. " +
        "If the context doesn't contain enough information, say so. " +
        "Cite which sources you used.";

    /// <summary>
    /// Factory that constructs a <see cref="ChatAnswerEngine"/> by resolving all dependencies
    /// from the provided <see cref="IServiceProvider"/>. Centralizes dependency wiring so new
    /// optional dependencies added to the constructor are threaded through automatically at
    /// every registration site.
    /// </summary>
    /// <remarks>
    /// Resolves <c>IChatClient</c> as required — throws at resolution time if none is registered.
    /// Callers that need to handle the missing-chat-client case gracefully (e.g., retrieval-only
    /// pipelines) must check for <c>IChatClient</c> registration themselves before invoking.
    /// </remarks>
    public static ChatAnswerEngine CreateFromServices(IServiceProvider serviceProvider) =>
        new(
            serviceProvider.GetRequiredService<IChatClient>(),
            serviceProvider.GetService<IConversationMemory>(),
            serviceProvider.GetService<IContextualCompressor>(),
            serviceProvider.GetService<IPromptObserver>());

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
        activity?.SetTag("source.count", sources.Count);
        activity?.SetTag("synthesis.strategy", opts.SynthesisStrategy.ToString());
        TagGenAi(activity, streaming: false);

        sources = await MaybeCompressAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        var (messages, chatOptions) = await BuildMessagesAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
            TagUsage(activity, response.Usage);
            return new RagResponse
            {
                Answer = response.Text ?? string.Empty,
                Sources = sources,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            sw.Stop();
            RagTelemetry.AskDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();

        using var activity = RagTelemetry.ActivitySource.StartActivity("ragnet.ask");
        activity?.SetTag("source.count", sources.Count);
        activity?.SetTag("synthesis.strategy", opts.SynthesisStrategy.ToString());
        TagGenAi(activity, streaming: true);

        sources = await MaybeCompressAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        // Emit post-compression sources so consumers see what the model actually saw.
        yield return new RagStreamingUpdate { Sources = sources };

        var (messages, chatOptions) = await BuildMessagesAsync(sources, query, opts, cancellationToken).ConfigureAwait(false);

        // C# async iterators cannot yield inside a try/catch, so we drive the enumerator manually:
        // MoveNextAsync() and Current are accessed outside yield, keeping yield clean.
        var enumerator = chatClient
            .GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var sw = Stopwatch.StartNew();
        bool hasNext;
        try
        {
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            while (hasNext)
            {
                var update = enumerator.Current;
                if (update.Text is not null)
                {
                    yield return new RagStreamingUpdate { TextDelta = update.Text };
                }

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    throw;
                }
            }
        }
        finally
        {
            sw.Stop();
            RagTelemetry.AskDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Applies contextual compression when configured and not skipped for this call.</summary>
    private async ValueTask<IReadOnlyList<SearchResult>> MaybeCompressAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts,
        CancellationToken cancellationToken)
    {
        if (compressor is null || opts.SkipCompression)
        {
            return sources;
        }

        return await compressor.CompressAsync(sources, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(List<ChatMessage> Messages, ChatOptions Options)> BuildMessagesAsync(
        IReadOnlyList<SearchResult> sources,
        string query,
        RagOptions opts,
        CancellationToken cancellationToken)
    {
        var context = string.Join("\n\n---\n\n",
            sources.Select((s, i) => $"[Source {i + 1}]\n{s.CompressedText ?? s.Chunk.Text}"));

        var systemPrompt = opts.SystemPrompt ?? DefaultSystemPrompt;

        var messages = new List<ChatMessage>();

        IReadOnlyList<ChatMessage>? history = null;
        if (opts.ConversationHistory is { Count: > 0 })
        {
            history = opts.ConversationHistory as IReadOnlyList<ChatMessage> ?? opts.ConversationHistory.ToList();
            if (memory is not null)
                history = await memory.ProcessAsync(history, cancellationToken).ConfigureAwait(false);
        }

        // Leading system messages from history (e.g. a prompt-hardening prefix) go FIRST
        // so they are not shadowed by the primary system prompt below.
        var historyStart = 0;
        if (history is not null)
        {
            while (historyStart < history.Count && history[historyStart].Role == ChatRole.System)
            {
                messages.Add(history[historyStart]);
                historyStart++;
            }
        }

        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        // Remaining history — user/assistant turns
        if (history is not null)
            for (var i = historyStart; i < history.Count; i++)
                messages.Add(history[i]);

        if (opts.SendSourcesAsToolResult)
        {
            AddSourcesAsToolResult(messages, context, query);
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.User, $"Context:\n{context}\n\nQuestion: {query}"));
        }

        var chatOptions = new ChatOptions();
        if (opts.Temperature.HasValue)
        {
            chatOptions.Temperature = opts.Temperature.Value;
        }

        // The prompt seam. Null unless something registered an observer, so generation is unchanged
        // when nothing is watching. Placed here rather than at either caller because both AskAsync
        // and AskStreamingAsync funnel through this method inside their own ragnet.ask span, so one
        // call site covers both paths and cannot drift apart from itself.
        promptObserver?.OnPromptAssembled(messages);

        return (messages, chatOptions);
    }

    /// <summary>
    /// Adds the query as a plain user turn and the sources as a tool result.
    /// </summary>
    /// <remarks>
    /// The assistant tool-call message is not decoration: a <c>Tool</c> message with nothing having
    /// requested it is a malformed exchange, and providers differ on whether they tolerate it. The
    /// call id is generated per request and echoed by the result so the pair is well-formed.
    /// </remarks>
    /// <param name="messages">The message list being built.</param>
    /// <param name="context">The assembled source text.</param>
    /// <param name="query">The user's question, which becomes the user turn on its own.</param>
    private void AddSourcesAsToolResult(List<ChatMessage> messages, string context, string query)
    {
        var callId = "call_" + _guidProvider.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        messages.Add(new ChatMessage(ChatRole.User, query));
        messages.Add(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent(
                callId,
                SourcesToolName,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["question"] = query })]));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, context)]));
    }

    /// <summary>
    /// Tags <paramref name="activity"/> with the OTel GenAI semantic-convention attributes that
    /// describe a chat completion call.
    /// </summary>
    /// <remarks>
    /// Pinned against GenAI semconv <b>v1.41.0</b> — the last version of
    /// <c>open-telemetry/semantic-conventions</c> tagged before the <c>gen_ai.*</c> definitions
    /// moved to the dedicated <c>open-telemetry/semantic-conventions-genai</c> repository, which
    /// as of this writing has cut no release of its own to pin against instead. See
    /// <c>https://github.com/open-telemetry/semantic-conventions/blob/v1.41.0/docs/gen-ai/gen-ai-spans.md</c>.
    /// Every attribute here is <c>Development</c>-stability in that revision — expect the spec to
    /// rename or reshape them; re-check this pin before assuming a name below still matches
    /// upstream. <c>gen_ai.provider.name</c> is used rather than the older <c>gen_ai.system</c>,
    /// which v1.41.0 already marks deprecated in favor of it.
    /// <para>
    /// Provider name and request model come from <see cref="ChatClientMetadata"/>, which
    /// <c>chatClient.GetService&lt;ChatClientMetadata&gt;()</c> may or may not expose — a bare
    /// test double typically returns <see langword="null"/>, in which case both are left unset
    /// rather than guessed.
    /// </para>
    /// </remarks>
    private void TagGenAi(Activity? activity, bool streaming)
    {
        activity?.SetTag("gen_ai.operation.name", "chat");
        if (streaming)
        {
            activity?.SetTag("gen_ai.request.stream", true);
        }

        var metadata = chatClient.GetService<ChatClientMetadata>();
        if (!string.IsNullOrWhiteSpace(metadata?.ProviderName))
        {
            activity?.SetTag("gen_ai.provider.name", metadata.ProviderName);
        }

        if (!string.IsNullOrWhiteSpace(metadata?.DefaultModelId))
        {
            activity?.SetTag("gen_ai.request.model", metadata.DefaultModelId);
        }
    }

    /// <summary>
    /// Tags <paramref name="activity"/> with <c>gen_ai.usage.input_tokens</c> /
    /// <c>gen_ai.usage.output_tokens</c> when the provider reported usage on the response.
    /// Only called from <see cref="AskAsync"/>, where <see cref="ChatResponse.Usage"/> is a
    /// single already-in-scope property; <see cref="AskStreamingAsync"/> would need to scan
    /// every update for a trailing <c>UsageContent</c> the way <c>CostTrackingChatClient</c>
    /// does for the cost ledger, which is that decorator's job, not this engine's — so the
    /// streaming span carries no usage tags.
    /// </summary>
    private static void TagUsage(Activity? activity, UsageDetails? usage)
    {
        if (usage?.InputTokenCount is { } inputTokens)
        {
            activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        }

        if (usage?.OutputTokenCount is { } outputTokens)
        {
            activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
        }
    }
}
