using System.Linq.Expressions;
using Rag.NET.Models;
using ZeroAlloc.Specification;

namespace Rag.NET.Retrieval.Specifications;

/// <summary>
/// Satisfied when every inner specification is. Holds the specs in an array of the concrete struct
/// type, so combining N of them boxes nothing per spec the way chaining <c>And</c> on the
/// interface does.
/// </summary>
internal sealed class AllOfSpec<TSpec>(TSpec[] specs) : ISpecification<SearchResult>
    where TSpec : struct, ISpecification<SearchResult>
{
    public bool IsSatisfiedBy(SearchResult r)
    {
        foreach (var spec in specs)
        {
            if (!spec.IsSatisfiedBy(r))
                return false;
        }
        return true;
    }

    public Expression<Func<SearchResult, bool>> ToExpression()
    {
        var parameter = Expression.Parameter(typeof(SearchResult), "r");
        Expression? body = null;
        foreach (var spec in specs)
        {
            var inner = spec.ToExpression();
            var call = new ParameterReplacer(inner.Parameters[0], parameter).Visit(inner.Body);
            body = body is null ? call : Expression.AndAlso(body, call);
        }
        return Expression.Lambda<Func<SearchResult, bool>>(body ?? Expression.Constant(true), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
