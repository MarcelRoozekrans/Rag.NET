using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Abstractions;

/// <summary>
/// Registers factory-built parsers in a way <c>AddParser&lt;TParser&gt;(replaces:)</c> can reach.
/// An extension rather than a member of <see cref="IRagBuilder"/> so the interface stays
/// non-breaking for existing implementers.
/// </summary>
public static class ParserFactoryRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TParser"/> as a singleton <see cref="IDocumentParser"/> built by
    /// <paramref name="factory"/>, and records a <see cref="ParserRegistration"/> marker so a later
    /// <c>AddParser&lt;T&gt;(replaces: typeof(TParser))</c> can remove it. Plain
    /// <c>AddSingleton&lt;IDocumentParser&gt;(sp =&gt; ...)</c> leaves <c>ImplementationType</c>
    /// <see langword="null"/>, so a replace cannot tell what it builds.
    /// </summary>
    /// <typeparam name="TParser">
    /// The concrete type <paramref name="factory"/> returns at runtime, and the type the package's
    /// <see cref="ParserClaim"/>s name. The marker records <typeparamref name="TParser"/>, and
    /// <c>replaces:</c> finds a parser by comparing that name with the claims' name, so an interface
    /// or base type here would leave the claims pointing at a type no marker names, and a replace
    /// would throw. An interface or abstract type is therefore rejected.
    /// </typeparam>
    /// <param name="builder">The builder to register on.</param>
    /// <param name="factory">Builds the parser.</param>
    /// <param name="replaces">
    /// Deliberately overrides this parser, exactly as <see cref="IRagBuilder.AddParser{TParser}"/>'s
    /// <c>replaces</c> does: its registration, marker and claims are removed first.
    /// </param>
    /// <param name="replacesTypeNames">
    /// The same override by full type name, for a parser whose assembly is not referenced. A name
    /// that matches neither a registration nor a claim is a no-op.
    /// </param>
    /// <remarks>
    /// Declares no <see cref="ParserClaim"/>s. Each package keeps declaring its own, labelled with
    /// the call the user actually wrote, such as <c>AddPdfParser(configure)</c>.
    /// </remarks>
    /// <returns>The same <paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TParser"/> is an interface or an abstract class. Nothing is added.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A replaced name has claims but no findable registration; see <c>ParserReplacement</c>.
    /// </exception>
    public static IRagBuilder AddParserFactory<TParser>(
        this IRagBuilder builder,
        Func<IServiceProvider, TParser> factory,
        Type? replaces = null,
        string[]? replacesTypeNames = null)
        where TParser : class, IDocumentParser
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        if (typeof(TParser).IsInterface || typeof(TParser).IsAbstract)
        {
            throw new ArgumentException(
                $"TParser must be the concrete parser type the factory returns, not '{typeof(TParser).FullName}'. " +
                "The registration marker records TParser, and replaces: matches it against the type the " +
                "package's ParserClaims name, so an interface or abstract type would make the parser unreachable.",
                nameof(factory));
        }

        ParserReplacement.Remove(builder.Services, replaces, replacesTypeNames);

        var descriptor = ServiceDescriptor.Singleton<IDocumentParser>(factory);
        builder.Services.Add(descriptor);
        builder.Services.AddSingleton(ParserRegistration.For<TParser>(descriptor));
        return builder;
    }
}
