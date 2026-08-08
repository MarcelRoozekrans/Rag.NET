using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Abstractions;

/// <summary>
/// Minimal abstraction over <see cref="Rag.NET.DependencyInjection.RagBuilder"/> for use in extension packages.
/// Extension packages that register services should extend this interface rather than <c>RagBuilder</c> directly.
/// </summary>
public interface IRagBuilder
{
    /// <summary>
    /// The underlying service collection. For advanced registration scenarios only.
    /// Prefer using typed builder methods (e.g., <see cref="AddParser{TParser}"/>, <see cref="UseReranking{TReranker}"/>).
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Registers a document parser. Multiple parsers can be registered; the pipeline
    /// selects the first one whose <c>CanParse</c> returns <see langword="true"/> for a given content type.
    /// </summary>
    /// <typeparam name="TParser">The <see cref="IDocumentParser"/> implementation to register.</typeparam>
    /// <param name="replaces">
    /// When set, deliberately overrides <paramref name="replaces"/>: its registration and its
    /// <see cref="ParserClaim"/> are removed before <typeparamref name="TParser"/> is registered, so
    /// <typeparamref name="TParser"/> wins selection instead of merely avoiding the conflict check.
    /// See <c>RagBuilder.AddParser</c>'s remarks for the full rationale.
    /// </param>
    /// <param name="replacesTypeNames">
    /// The same override as <paramref name="replaces"/>, by full type name rather than by
    /// <see cref="Type"/>, for a caller that cannot reference the replaced parser's assembly — an
    /// optional package that may not even be installed. A name with no matching registration is a
    /// no-op, not an error. See <c>RagBuilder.AddParser</c>'s remarks for the full rationale.
    /// </param>
    IRagBuilder AddParser<TParser>(Type? replaces = null, string[]? replacesTypeNames = null)
        where TParser : class, IDocumentParser;

    /// <summary>Registers a singleton <see cref="IReranker"/> of type <typeparamref name="TReranker"/>.</summary>
    IRagBuilder UseReranking<TReranker>() where TReranker : class, IReranker;
}
