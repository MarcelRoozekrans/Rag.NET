using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Security;

/// <summary>
/// Wraps an <see cref="IAnswerEngine"/> and prepends a hardening system message
/// before every LLM call, instructing the model to treat retrieved content as data only.
/// </summary>
public sealed class PromptHardeningAnswerEngineDecorator(
    IAnswerEngine inner,
    PromptHardeningOptions options) : IAnswerEngine
{
    public Task<RagResponse> AskAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options_param = null,
        CancellationToken cancellationToken = default)
        => inner.AskAsync(query, sources, WithPrefix(options_param), cancellationToken);

    public IAsyncEnumerable<RagStreamingUpdate> AskStreamingAsync(
        string query,
        IReadOnlyList<SearchResult> sources,
        RagOptions? options_param = null,
        CancellationToken cancellationToken = default)
        => inner.AskStreamingAsync(query, sources, WithPrefix(options_param), cancellationToken);

    private RagOptions WithPrefix(RagOptions? opts)
    {
        if (string.IsNullOrEmpty(options.SystemPrefix))
            return opts ?? new RagOptions();

        var result = opts is null ? new RagOptions() : ShallowCopy(opts);
        var prefixMessage = new ChatMessage(ChatRole.System, options.SystemPrefix);
        var existing = result.ConversationHistory;
        var merged = new List<ChatMessage> { prefixMessage };
        if (existing is { Count: > 0 })
            merged.AddRange(existing);
        result.ConversationHistory = merged;
        return result;
    }

    private static RagOptions ShallowCopy(RagOptions src) => new()
    {
        TopK = src.TopK,
        MinScore = src.MinScore,
        UseHybridSearch = src.UseHybridSearch,
        UseLostInTheMiddleReordering = src.UseLostInTheMiddleReordering,
        UseRedundancyFilter = src.UseRedundancyFilter,
        RedundancyThreshold = src.RedundancyThreshold,
        MetadataFilter = src.MetadataFilter,
        SystemPrompt = src.SystemPrompt,
        Temperature = src.Temperature,
        ConversationHistory = src.ConversationHistory,
        SynthesisStrategy = src.SynthesisStrategy,
        MapReduceOptions = src.MapReduceOptions,
        RefineOptions = src.RefineOptions,
    };
}
