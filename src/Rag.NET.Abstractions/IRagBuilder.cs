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
    IRagBuilder AddParser<TParser>() where TParser : class, IDocumentParser;

    /// <summary>Registers a singleton <see cref="IReranker"/> of type <typeparamref name="TReranker"/>.</summary>
    IRagBuilder UseReranking<TReranker>() where TReranker : class, IReranker;
}
