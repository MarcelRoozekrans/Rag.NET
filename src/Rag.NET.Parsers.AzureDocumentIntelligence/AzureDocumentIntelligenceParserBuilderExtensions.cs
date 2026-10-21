using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.AzureDocumentIntelligence;

/// <summary>
/// Registration extensions for <see cref="AzureDocumentIntelligenceDocumentParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claims are declared by hand.</b> The parser is registered through a factory, because it
/// needs the endpoint, the credential and the configured options, and it resolves
/// <see cref="ICostLedger"/> and <see cref="ILogger{TCategoryName}"/> from the container without
/// either appearing on these signatures. A factory bypasses <c>AddParser&lt;TParser&gt;()</c>'s
/// automatic <see cref="ParserClaim"/>s, so these methods declare one per content type the
/// configured <see cref="AzureDocumentIntelligenceParserOptions.Formats"/> claim. That is what
/// makes <c>AddRagNet</c> refuse, at startup, an opted-in format another parser also claims:
/// <see cref="AzureDocumentIntelligenceFormats.Word"/> alongside <c>AddWordParser()</c>, for
/// instance.
/// </para>
/// <para>
/// <c>DocumentIntelligenceClientOptions</c> is intentionally not exposed here; it is the
/// analyzer's transport-injection test seam, not a supported configuration surface.
/// </para>
/// </remarks>
public static class AzureDocumentIntelligenceParserBuilderExtensions
{
    private const string RegistrationMethod = "AddAzureDocumentIntelligenceParser";

    /// <summary>
    /// Registers the Azure Document Intelligence parser, authenticating with an API key.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The resource's API key.</param>
    /// <param name="configure">Optional formats, size cap and analyzer settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/>, <paramref name="endpoint"/> or <paramref name="credential"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The configured <see cref="AzureDocumentIntelligenceParserOptions.Formats"/> claim no
    /// content type, or the analyzer's model id is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> is not positive, or the
    /// analyzer's price or polling interval is negative.
    /// </exception>
    public static TBuilder AddAzureDocumentIntelligenceParser<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        AzureKeyCredential credential,
        Action<AzureDocumentIntelligenceParserOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var options = BuildOptions(configure);
        return Register(builder, options, sp => new AzureDocumentIntelligenceAnalyzer(
            endpoint,
            credential,
            options.Analyzer,
            sp.GetService<ICostLedger>(),
            sp.GetService<ILogger<AzureDocumentIntelligenceDocumentParser>>(),
            clientOptions: null));
    }

    /// <summary>
    /// Registers the Azure Document Intelligence parser, authenticating with a
    /// <see cref="TokenCredential"/> (OAuth / managed identity).
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The token credential to authenticate with.</param>
    /// <param name="configure">Optional formats, size cap and analyzer settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/>, <paramref name="endpoint"/> or <paramref name="credential"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The configured <see cref="AzureDocumentIntelligenceParserOptions.Formats"/> claim no
    /// content type, or the analyzer's model id is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="AzureDocumentIntelligenceParserOptions.MaxBytes"/> is not positive, or the
    /// analyzer's price or polling interval is negative.
    /// </exception>
    public static TBuilder AddAzureDocumentIntelligenceParser<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        TokenCredential credential,
        Action<AzureDocumentIntelligenceParserOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var options = BuildOptions(configure);
        return Register(builder, options, sp => new AzureDocumentIntelligenceAnalyzer(
            endpoint,
            credential,
            options.Analyzer,
            sp.GetService<ICostLedger>(),
            sp.GetService<ILogger<AzureDocumentIntelligenceDocumentParser>>(),
            clientOptions: null));
    }

    /// <summary>
    /// Registers the parser by factory and declares a <see cref="ParserClaim"/> for each content
    /// type its formats claim.
    /// </summary>
    private static TBuilder Register<TBuilder>(
        TBuilder builder,
        AzureDocumentIntelligenceParserOptions options,
        Func<IServiceProvider, AzureDocumentIntelligenceAnalyzer> createAnalyzer)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<IDocumentParser>(sp =>
            new AzureDocumentIntelligenceDocumentParser(createAnalyzer(sp), options));

        foreach (var contentType in options.Formats.ContentTypes())
        {
            builder.Services.AddSingleton(ParserClaim.For<AzureDocumentIntelligenceDocumentParser>(
                contentType, RegistrationMethod));
        }

        return builder;
    }

    /// <summary>
    /// Applies <paramref name="configure"/> and validates the result <i>here</i>, at registration.
    /// The parser is built in a DI factory the container does not invoke until first resolution,
    /// so leaving the checks to its constructor would let a bad option register silently and fail
    /// later, mid-ingestion. Nothing is added to the service collection when a check fails.
    /// </summary>
    private static AzureDocumentIntelligenceParserOptions BuildOptions(
        Action<AzureDocumentIntelligenceParserOptions>? configure)
    {
        var options = new AzureDocumentIntelligenceParserOptions();
        configure?.Invoke(options);

        if (options.Formats.ContentTypes().Count == 0)
        {
            throw new ArgumentException(
                $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.Formats)} " +
                "must name at least one format; a parser that claims nothing would never be used.",
                nameof(configure));
        }

        if (options.MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), options.MaxBytes,
                $"{nameof(AzureDocumentIntelligenceParserOptions)}.{nameof(AzureDocumentIntelligenceParserOptions.MaxBytes)} " +
                "must be positive.");
        }

        AzureDocumentIntelligenceAnalyzerSettings.Validate(options.Analyzer, nameof(configure));
        return options;
    }
}
