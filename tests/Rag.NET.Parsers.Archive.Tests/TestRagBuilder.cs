using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Archive.Tests;

/// <summary>
/// The smallest thing <c>AddArchiveParser</c> will accept, so the registration can be asserted
/// without this package's tests taking a dependency on <c>Rag.NET</c>.
/// </summary>
/// <remarks>
/// <see cref="AddParser{TParser}"/> and <see cref="UseReranking{TReranker}"/> throw rather than being
/// implemented: <c>AddArchiveParser</c> reaches for neither, and a stub that silently did nothing
/// would let a future version start using one and go unnoticed.
/// </remarks>
internal sealed class TestRagBuilder : IRagBuilder
{
    public IServiceCollection Services { get; } = new ServiceCollection();

    public IRagBuilder AddParser<TParser>(Type? replaces = null, string[]? replacesTypeNames = null)
        where TParser : class, IDocumentParser =>
        throw new NotSupportedException("AddArchiveParser is not expected to route through AddParser<T>().");

    public IRagBuilder UseReranking<TReranker>()
        where TReranker : class, IReranker =>
        throw new NotSupportedException("AddArchiveParser is not expected to register a reranker.");
}
