using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rag.NET.Abstractions;
using Rag.NET.AnswerEngines;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.AnswerGeneration;

/// <summary>
/// FLARE (Forward-Looking Active Retrieval) engine: generates the answer one sentence at a
/// time, scores each sentence's confidence against the current context, and — when confidence
/// drops below <see cref="FlareOptions.ConfidenceThreshold"/> — performs a lookahead retrieval
/// (original query + low-confidence sentence), merges the fresh sources into the context, and
/// regenerates the sentence once.
/// </summary>
/// <remarks>
/// Error posture: degraded, never broken — retrieval failures keep the original sentence and
/// continue with the existing context; scorer failures are treated as fully confident (1.0).
/// Sentence-generation LLM failures propagate (nothing sensible to degrade to).
/// </remarks>
public sealed class FlareAnswerEngine : IAnswerEngine
{
    private const string DefaultSystemPrompt =
        "Answer the user's question using only the provided context. " +
        "If the context doesn't contain enough information, say so.";

    private const string DoneToken = "<DONE>";

    /// <summary>
    /// FLARE's own framing, always appended after any caller system prompt.
    /// </summary>
    /// <remarks>
    /// <b>This is mechanism, not style, which is why a caller cannot displace it.</b> FLARE emits one
    /// sentence per call and feeds the growing answer back in, so an instruction written for a complete
    /// reply — "end with this sentence", "reply in JSON" — is actively harmful applied per fragment: the
    /// model satisfies it every time, the satisfied text becomes "answer so far", and it never emits
    /// <see cref="DoneToken"/>. Appended last so it is the most recent instruction the model reads.
    /// </remarks>
    private const string FragmentProtocol =
        "You are writing ONE sentence at a time as part of a longer answer that is assembled from " +
        "your replies. Reply with exactly one sentence, or with only " + DoneToken + " if the answer " +
        "is complete. These replies are fragments, not a complete reply: do not add closing or " +
        "summary sentences, and do not apply any end-of-reply formatting instruction to them.";

    private readonly IChatClient _chatClient;
    private readonly IRetriever _retriever;
    private readonly IConfidenceScorer _scorer;
    private readonly FlareOptions _options;
    private readonly ILogger<FlareAnswerEngine> _logger;

    public FlareAnswerEngine(
        IChatClient chatClient,
        IRetriever retriever,
        IConfidenceScorer scorer,
        FlareOptions options,
        ILogger<FlareAnswerEngine>? logger = null)
    {
        _chatClient = chatClient;
        _retriever = retriever;
        _scorer = scorer;
        _options = options;
        _logger = logger ?? NullLogger<FlareAnswerEngine>.Instance;
    }

    /// <summary>
    /// Factory that constructs a <see cref="FlareAnswerEngine"/> by resolving all dependencies
    /// from the provided <see cref="IServiceProvider"/>. Centralizes dependency wiring so new
    /// optional dependencies added to the constructor are threaded through automatically at
    /// every registration site.
    /// </summary>
    public static FlareAnswerEngine CreateFromServices(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetService<FlareOptions>() ?? new FlareOptions();
        return new(
            options.ChatClient ?? serviceProvider.GetRequiredService<IChatClient>(),
            serviceProvider.GetRequiredService<IRetriever>(),
            serviceProvider.GetRequiredService<IConfidenceScorer>(),
            options,
            serviceProvider.GetService<ILogger<FlareAnswerEngine>>());
    }

    public async Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new RagOptions();
        var chatOptions = BuildChatOptions(opts);
        var context = sources;
        var sentences = new List<string>();
        var retrievalsUsed = 0;

        for (var i = 0; i < _options.MaxSentences; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partialAnswer = string.Join(" ", sentences);
            var sentence = await GenerateSentenceAsync(query, partialAnswer, context, opts, chatOptions, cancellationToken).ConfigureAwait(false);
            if (sentence is null)
                break;

            var score = await ScoreSentenceSafeAsync(sentence, partialAnswer, context, cancellationToken).ConfigureAwait(false);
            if (score < _options.ConfidenceThreshold && retrievalsUsed < _options.MaxRetrievals)
            {
                retrievalsUsed++;
                var merged = await TryLookaheadRetrievalAsync(query, sentence, context, cancellationToken).ConfigureAwait(false);
                if (merged is not null)
                {
                    context = merged;
                    // Accept the regenerated sentence regardless of its score — one
                    // regeneration per sentence, no infinite loops. A <DONE>/empty
                    // regeneration keeps the original sentence.
                    var regenerated = await GenerateSentenceAsync(query, partialAnswer, context, opts, chatOptions, cancellationToken).ConfigureAwait(false);
                    if (regenerated is not null)
                        sentence = regenerated;
                }
            }

            sentences.Add(sentence);
        }

        return new RagResponse
        {
            Answer = string.Join(" ", sentences),
            Sources = context,
        };
    }

    public async IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Delegate to AskAsync — the emitted Sources must reflect all mid-generation
        // lookahead retrievals, which are only known once the sentence loop completes
        // (same rationale as MapReduceAnswerEngine/RefineAnswerEngine delegation).
        var response = await AskAsync(query, sources, options, cancellationToken).ConfigureAwait(false);
        yield return new RagStreamingUpdate { Sources = response.Sources };
        yield return new RagStreamingUpdate { TextDelta = response.Answer };
    }

    /// <summary>
    /// Generates the next answer sentence. Returns <see langword="null"/> when the model
    /// signals completion (<c>&lt;DONE&gt;</c>) or returns an empty response.
    /// </summary>
    private async Task<string?> GenerateSentenceAsync(
        string query,
        string partialAnswer,
        IReadOnlyList<SearchResult> context,
        RagOptions opts,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var messages = BuildMessages(query, partialAnswer, context, opts);
        var response = await _chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);

        var text = response.Text?.Trim();
        // A response STARTING with the done token counts as done ("<DONE>." and similar
        // decorations leak from some models). A TRAILING done token after a sentence is
        // stripped below — the sentence is kept and the model is asked to continue; it
        // is expected to reply with the bare done token on the next iteration.
        if (string.IsNullOrEmpty(text) || text.StartsWith(DoneToken, StringComparison.Ordinal))
            return null;

        var sentence = ExtractFirstSentence(text);
        if (sentence.EndsWith(DoneToken, StringComparison.Ordinal))
            sentence = sentence[..^DoneToken.Length].Trim();

        return sentence.Length == 0 ? null : sentence;
    }

    /// <summary>
    /// Scores the sentence via the configured <see cref="IConfidenceScorer"/>. The default
    /// self-assessment scorer fails open internally; custom scorers may still throw — those
    /// exceptions are caught here, logged, and treated as fully confident (1.0).
    /// </summary>
    private async Task<double> ScoreSentenceSafeAsync(
        string sentence,
        string partialAnswer,
        IReadOnlyList<SearchResult> context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _scorer.ScoreAsync(sentence, partialAnswer, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AnswerEngineLog.FlareScorerThrew(_logger, ex);
            return 1.0;
        }
    }

    /// <summary>
    /// Performs the lookahead retrieval (original query + low-confidence sentence) and merges
    /// the results into the current context. Returns <see langword="null"/> on any retrieval
    /// failure — the caller keeps the sentence and continues with the existing context.
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>?> TryLookaheadRetrievalAsync(
        string query,
        string sentence,
        IReadOnlyList<SearchResult> context,
        CancellationToken cancellationToken)
    {
        var lookaheadQuery = $"{query}\n{sentence}";
        // Default lookahead is a PLAIN retrieval: the query already embeds a synthetic
        // document (draft sentence), so HyDE / multi-query expansion on top would spawn
        // hypotheses-of-a-hypothesis — hidden LLM calls for little gain. Reranking stays
        // enabled. FlareOptions.LookaheadRetrievalOptions overrides verbatim (incl. TopK).
        var retrievalOptions = _options.LookaheadRetrievalOptions ?? new RetrievalOptions
        {
            TopK = _options.LookaheadTopK,
            UseHyde = false,
            UseMultiQuery = false,
        };
        try
        {
            var result = await _retriever.RetrieveAsync(lookaheadQuery, retrievalOptions, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                AnswerEngineLog.FlareLookaheadFailed(_logger, result.Error.GetType().Name);
                return null;
            }

            if (result.Value.Count == 0)
            {
                // Nothing new to ground a regeneration on — keep the sentence and do not
                // burn a regeneration LLM call.
                AnswerEngineLog.FlareLookaheadEmpty(_logger);
                return null;
            }

            return MergeSources(context, result.Value);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AnswerEngineLog.FlareLookaheadThrew(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// Merges retrieved results into the current context, deduplicating by
    /// <c>(DocumentId, ChunkIndex)</c> and keeping the highest-scoring instance,
    /// ordered by score descending (same semantics as <c>DeepResearchRetriever</c>).
    /// </summary>
    private static IReadOnlyList<SearchResult> MergeSources(
        IReadOnlyList<SearchResult> context,
        IReadOnlyList<SearchResult> retrieved)
    {
        var merged = new List<SearchResult>(context.Count + retrieved.Count);
        merged.AddRange(context);
        merged.AddRange(retrieved);

        var seen = new Dictionary<(string, int), SearchResult>();
        foreach (ref readonly var r in CollectionsMarshal.AsSpan(merged))
        {
            var key = (r.Chunk.DocumentId.Value, r.Chunk.ChunkIndex);
            if (!seen.TryGetValue(key, out var existing) || r.Score > existing.Score)
                seen[key] = r;
        }

        return [.. seen.Values.OrderByDescending(r => r.Score)];
    }

    /// <summary>
    /// Takes the first sentence from the response: a simple terminator scan for
    /// <c>.</c>/<c>!</c>/<c>?</c> followed by whitespace or end-of-text. Deliberately
    /// simple — no abbreviation handling; a slightly-too-short sentence merely costs
    /// one extra loop iteration.
    /// </summary>
    private static string ExtractFirstSentence(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '.' || c == '!' || c == '?') &&
                (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1])))
            {
                return text[..(i + 1)];
            }
        }

        return text;
    }

    private static List<ChatMessage> BuildMessages(
        string query,
        string partialAnswer,
        IReadOnlyList<SearchResult> context,
        RagOptions opts)
    {
        // Single-message layout: the full prompt (context + question + partial answer) is
        // rebuilt every iteration. A multi-message layout that keeps the context prefix
        // stable across iterations (enabling provider-side prompt caching) is a future
        // optimization — deliberately out of scope here.
        var contextText = string.Join("\n\n---\n\n",
            context.Select((s, i) => $"[Source {i + 1}]\n{s.CompressedText ?? s.Chunk.Text}"));

        var userText =
            $"Context:\n{contextText}\n\n" +
            $"Question: {query}\n\n" +
            $"Answer so far:\n{(partialAnswer.Length == 0 ? "(nothing yet)" : partialAnswer)}\n\n" +
            "Continue the answer with EXACTLY ONE additional sentence. " +
            $"If the answer is complete, reply with only: {DoneToken}";

        return
        [
            new(ChatRole.System, BuildSystemPrompt(opts)),
            new(ChatRole.User, userText),
        ];
    }

    /// <summary>The caller's system prompt, if any, followed by FLARE's fragment protocol.</summary>
    private static string BuildSystemPrompt(RagOptions opts) =>
        $"{opts.SystemPrompt ?? DefaultSystemPrompt}\n\n{FragmentProtocol}";

    private static ChatOptions BuildChatOptions(RagOptions opts)
    {
        // MaxOutputTokens bounds rambling models — only one sentence is kept per call,
        // so anything beyond ~150 tokens is discarded output paid for nothing.
        var chatOptions = new ChatOptions { MaxOutputTokens = 150 };
        if (opts.Temperature.HasValue)
            chatOptions.Temperature = opts.Temperature.Value;
        return chatOptions;
    }
}
