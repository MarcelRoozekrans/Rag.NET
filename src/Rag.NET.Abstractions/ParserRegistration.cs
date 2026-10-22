using Microsoft.Extensions.DependencyInjection;

namespace Rag.NET.Abstractions;

/// <summary>
/// Marks an <see cref="IDocumentParser"/> factory registration with the type it builds, so
/// <c>AddParser&lt;TParser&gt;(replaces:)</c> can find and remove it. Registered as a singleton
/// instance by <see cref="ParserFactoryRegistrationExtensions.AddParserFactory{TParser}"/>.
/// </summary>
/// <param name="ParserTypeName">
/// The full type name of the parser the factory builds, matched against a replaced parser's
/// <see cref="Type.FullName"/> for the same reason <see cref="ParserClaim.ParserTypeName"/> is:
/// short names would collapse two distinct parsers that share one.
/// </param>
/// <param name="Descriptor">
/// The <see cref="IDocumentParser"/> factory descriptor this marker describes, by reference. A
/// factory descriptor does not reveal the type it produces without resolving it, which is the gap
/// this record closes: removal looks the descriptor up here rather than inspecting
/// <see cref="ServiceDescriptor.ImplementationType"/>, which is <see langword="null"/> for it.
/// </param>
public sealed record ParserRegistration(string ParserTypeName, ServiceDescriptor Descriptor)
{
    /// <summary>
    /// Creates a marker for <typeparamref name="TParser"/>, named by its
    /// <see cref="Type.FullName"/>.
    /// </summary>
    /// <typeparam name="TParser">The parser type the factory builds.</typeparam>
    /// <param name="descriptor">The factory descriptor to mark.</param>
    public static ParserRegistration For<TParser>(ServiceDescriptor descriptor)
        where TParser : class, IDocumentParser =>
        new(typeof(TParser).FullName ?? typeof(TParser).Name, descriptor);
}
