using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Rag.NET.Logging;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using Rag.NET.Retrieval;
using Rag.NET.Retrieval.Behaviors;
using Rag.NET.Retrieval.Specifications;
using ZeroAlloc.Inject;
using ZeroAlloc.Results;
using ZeroAlloc.Specification;

namespace Rag.NET.SelfQuery;

[Singleton]
public sealed class SelfQueryBehavior : IRetrievalBehavior
{
    [Inject(Required = false)] public IChatClient? ChatClient { get; set; }
    [Inject(Required = false)] public SelfQueryOptions? SelfQueryOptions { get; set; }
    [Inject(Required = false)] public ILogger<SelfQueryBehavior>? Logger { get; set; }

    public async ValueTask<IReadOnlyList<SearchResult>> HandleAsync(
        RetrievalContext ctx, CancellationToken ct,
        Func<RetrievalContext, CancellationToken, ValueTask<IReadOnlyList<SearchResult>>> next)
    {
        if (!ctx.Options.UseSelfQuery || ChatClient is null || SelfQueryOptions is null)
            return await next(ctx, ct).ConfigureAwait(false);

        var result = await ParseAsync(ctx.Query, ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var output = result.Value;
            var filter = BuildFilter(output.Filters);

            return await next(ctx with
            {
                Options = ctx.Options with
                {
                    UseSelfQuery = false,
                    EmbeddingTextOverride = output.Query,
                    Filter = filter ?? ctx.Options.Filter,
                }
            }, ct).ConfigureAwait(false);
        }
        else
        {
            RagPipelineLog.SelfQueryFailed(ctx.Logger, ctx.Query, result.Error);
            return await next(ctx with { Options = ctx.Options with { UseSelfQuery = false } }, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<Result<SelfQueryOutput>> ParseAsync(string question, CancellationToken ct)
    {
        try
        {
            var prompt = BuildPrompt(question);
            ChatMessage[] messages = [new(ChatRole.User, prompt)];
            var response = await ChatClient!.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);

            // Fenced or preambled replies used to land here whole, throw, and disable self-query
            // for the request with only a warning to show for it.
            var json = LlmJsonExtractor.Extract(response.Text ?? "{}", LlmJsonPayloadKind.Object);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? question : question;
            var filters = new List<KeyValuePair<string, string>>();

            // Only an ARRAY is enumerable here. A model returning the more natural
            // {"topic":"finance"} used to make EnumerateArray throw InvalidOperationException --
            // which the JsonException catch below does not cover, so it escaped HandleAsync and
            // failed the whole retrieval rather than degrading. Degrading is the stated intent: see
            // the comment above the extractor about malformed replies costing only a warning.
            if (root.TryGetProperty("filters", out var filtersProp)
                && filtersProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in filtersProp.EnumerateArray())
                {
                    var key = f.TryGetProperty("key", out var k) ? k.GetString() : null;
                    var value = f.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (key is not null && value is not null)
                        filters.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            return Result<SelfQueryOutput>.Success(new SelfQueryOutput(query, filters));
        }
        catch (JsonException ex)
        {
            return Result<SelfQueryOutput>.Failure(ex.Message);
        }
    }

    // Schema is injected into the LLM prompt only — the LLM is trusted to return schema-valid keys.
    // No server-side key filtering is applied here, unlike LlmMetadataExtractionBehavior.
    private static ISpecification<SearchResult>? BuildFilter(IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        ISpecification<SearchResult>? result = null;
        foreach (var (key, value) in filters)
        {
            var spec = new HasTagSpec(key, value);
            result = result is null ? spec : result.And(spec);
        }
        return result;
    }

    private string BuildPrompt(string question)
    {
        if (SelfQueryOptions!.Schema is { Count: > 0 } schema)
        {
            var fields = string.Join(", ", schema.Select(a => $"{a.Name} ({a.Description})"));
            return $$"""
                Parse this question into a search query and metadata filters.
                Available metadata fields: {{fields}}.
                Return JSON: {"query": "...", "filters": [{"key": "...", "value": "..."}]}.
                Only include filters for the listed fields. Filters may be an empty array.

                Question: {{question}}
                """;
        }

        return $$"""
            Parse this question into a search query and metadata filters.
            Return JSON: {"query": "...", "filters": [{"key": "...", "value": "..."}]}.
            Filters may be an empty array.

            Question: {{question}}
            """;
    }
}

file static class SpecificationCompositionExtensions
{
    internal static ISpecification<T> And<T>(this ISpecification<T> left, ISpecification<T> right) =>
        new AndSpec<T>(left, right);

    private sealed class AndSpec<T>(ISpecification<T> left, ISpecification<T> right) : ISpecification<T>
    {
        public bool IsSatisfiedBy(T candidate) =>
            left.IsSatisfiedBy(candidate) && right.IsSatisfiedBy(candidate);

        public Expression<Func<T, bool>> ToExpression()
        {
            var param = Expression.Parameter(typeof(T), "r");
            var leftBody = Expression.Invoke(left.ToExpression(), param);
            var rightBody = Expression.Invoke(right.ToExpression(), param);
            return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(leftBody, rightBody), param);
        }
    }
}
