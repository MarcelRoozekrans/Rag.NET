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
            RagPipelineLog.SelfQueryCompleted(ctx.Logger, ctx.Query, output.Filters.Count);

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
            var json = response.Text ?? "{}";

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() ?? question : question;
            var filters = new List<KeyValuePair<string, string>>();

            if (root.TryGetProperty("filters", out var filtersProp))
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

    private static ISpecification<SearchResult>? BuildFilter(IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        if (filters.Count == 0)
            return null;

        var specs = filters.Select(f => new HasTagSpec(f.Key, f.Value)).ToArray();
        return new AllTagsSpec(specs);
    }

    private sealed class AllTagsSpec(HasTagSpec[] specs) : ISpecification<SearchResult>
    {
        public bool IsSatisfiedBy(SearchResult candidate) =>
            Array.TrueForAll(specs, s => s.IsSatisfiedBy(candidate));

        public System.Linq.Expressions.Expression<Func<SearchResult, bool>> ToExpression()
        {
            var param = System.Linq.Expressions.Expression.Parameter(typeof(SearchResult), "r");
            System.Linq.Expressions.Expression? body = null;
            foreach (var spec in specs)
            {
                var compiled = spec.ToExpression();
                var visitor = new ParameterReplaceVisitor(compiled.Parameters[0], param);
                var replaced = visitor.Visit(compiled.Body);
                body = body is null ? replaced : System.Linq.Expressions.Expression.AndAlso(body, replaced);
            }
            body ??= System.Linq.Expressions.Expression.Constant(true);
            return System.Linq.Expressions.Expression.Lambda<Func<SearchResult, bool>>(body, param);
        }
    }

    private sealed class ParameterReplaceVisitor(
        System.Linq.Expressions.ParameterExpression from,
        System.Linq.Expressions.ParameterExpression to)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(
            System.Linq.Expressions.ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
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
