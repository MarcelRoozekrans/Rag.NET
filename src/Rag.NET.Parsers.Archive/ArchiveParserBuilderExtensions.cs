using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Archive;

public static class ArchiveParserBuilderExtensions
{
    /// <summary>
    /// The call named in a <see cref="ParserClaim"/>, and therefore in the startup error a
    /// content-type conflict produces. It is the call a user wrote, not the private
    /// <c>Register</c> both overloads funnel through.
    /// </summary>
    private const string RegistrationMethod = "AddArchiveParser()";

    /// <summary>
    /// Registers <see cref="ZipDocumentParser"/> with default <see cref="ArchiveParserOptions"/>. The
    /// parser receives the parser collection deferred, because it is part of the collection it
    /// dispatches entries to and eager constructor injection of
    /// <c>IEnumerable&lt;IDocumentParser&gt;</c> would be a circular dependency.
    /// </summary>
    public static TBuilder AddArchiveParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder =>
        Register(builder, new ArchiveParserOptions(), registerOptions: false);

    /// <summary>
    /// Registers <see cref="ZipDocumentParser"/> with configured <see cref="ArchiveParserOptions"/>,
    /// which bound how much one archive may cost to read and how far nested containers are followed.
    /// Calling either overload more than once registers multiple parser instances — the pipeline uses
    /// the first whose <c>CanParse</c> matches (first registration wins at parse time), while the
    /// <see cref="ArchiveParserOptions"/> singleton resolved from DI is the most recently registered
    /// one (last wins); each parser instance keeps the options it was configured with.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A bound was asked for beyond its ceiling, or as a negative number.
    /// </exception>
    public static TBuilder AddArchiveParser<TBuilder>(this TBuilder builder, Action<ArchiveParserOptions>? configure)
        where TBuilder : IRagBuilder
    {
        var options = new ArchiveParserOptions();
        configure?.Invoke(options);

        // Validation lives on the options type rather than here, unlike AddEmailParser. The ceilings
        // and the messages that quote them are one thing, and splitting them would let a future
        // ceiling change land on the const without the message that names it.
        options.ThrowIfOutOfRange(nameof(configure));
        return Register(builder, options, registerOptions: true);
    }

    private static TBuilder Register<TBuilder>(TBuilder builder, ArchiveParserOptions options, bool registerOptions)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (registerOptions)
            builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<IDocumentParser>(sp => new ZipDocumentParser(
            ResolveParsersLater(sp),
            sp.GetService<ILogger<ZipDocumentParser>>(),
            options));

        // One claim per claimed content type, and exactly these two. Declared rather than discovered
        // because the registration above is a factory lambda, so ServiceDescriptor.ImplementationType
        // is null and the claim cannot be read back off the descriptor.
        builder.Services.AddSingleton(ParserClaim.For<ZipDocumentParser>(
            ZipDocumentParser.ZipContentType, RegistrationMethod));
        builder.Services.AddSingleton(ParserClaim.For<ZipDocumentParser>(
            ZipDocumentParser.ZipCompressedContentType, RegistrationMethod));
        return builder;
    }

    /// <summary>
    /// A deferred view over the registered parsers: an iterator, so nothing is resolved until the
    /// parser enumerates it during entry dispatch — long after construction.
    /// </summary>
    /// <remarks>
    /// <c>Rag.NET.Parsers.Email</c> does this with an <c>IEnumerable&lt;IDocumentParser&gt;</c>
    /// implementation instead. An iterator method is the same deferral without a second copy of that
    /// type in this package, and without its <c>#pragma</c>. Resolution goes through the provider the
    /// factory was handed, which assumes parsers are registered as singletons (as
    /// <c>IRagBuilder.AddParser</c> and the registration above both do) — a scoped parser registration
    /// would resolve from the root scope.
    /// </remarks>
    private static IEnumerable<IDocumentParser> ResolveParsersLater(IServiceProvider provider)
    {
        foreach (var parser in provider.GetServices<IDocumentParser>())
        {
            yield return parser;
        }
    }
}
