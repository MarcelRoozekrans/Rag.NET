using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence;

/// <summary>
/// Registration extensions putting <see cref="AzureDocumentIntelligenceOcrEngine"/> behind the
/// PDF parser's document-level OCR seam.
/// </summary>
/// <remarks>
/// Both overloads are thin wrappers over <c>UseDocumentOcrEngine(Func&lt;IServiceProvider,
/// IDocumentOcrEngine&gt;)</c>. That is what makes the engine resolve <see cref="ICostLedger"/>
/// and <see cref="ILogger{TCategoryName}"/> from the container without them appearing on this
/// signature — and it inherits the "two OCR engines configured" guard for free, in either
/// registration order.
/// <para>
/// No <c>&lt;EnableOcr&gt;</c> compile gate applies. That gate exists for Tesseract's native
/// binaries and traineddata files; a managed REST client has neither, and gating on it would
/// force an Azure-only consumer to pull Tesseract's native payload.
/// </para>
/// <para>
/// <c>DocumentIntelligenceClientOptions</c> is intentionally not exposed here — it is the
/// engine's transport-injection test seam, not a supported configuration surface.
/// </para>
/// </remarks>
public static class AzureDocumentIntelligenceBuilderExtensions
{
    /// <summary>
    /// Registers Azure Document Intelligence as the PDF parser's document-level OCR engine,
    /// authenticating with an API key.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The resource's API key.</param>
    /// <param name="configure">Optional model, pricing and polling settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder UseAzureDocumentIntelligenceOcr<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        AzureKeyCredential credential,
        Action<AzureDocumentIntelligenceOcrOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var options = BuildOptions(configure);
        return builder.UseDocumentOcrEngine(sp => new AzureDocumentIntelligenceOcrEngine(
            endpoint,
            credential,
            options,
            sp.GetService<ICostLedger>(),
            sp.GetService<ILogger<AzureDocumentIntelligenceOcrEngine>>()));
    }

    /// <summary>
    /// Registers Azure Document Intelligence as the PDF parser's document-level OCR engine,
    /// authenticating with a <see cref="TokenCredential"/> (OAuth / managed identity).
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">The Document Intelligence resource endpoint.</param>
    /// <param name="credential">The token credential to authenticate with.</param>
    /// <param name="configure">Optional model, pricing and polling settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder UseAzureDocumentIntelligenceOcr<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        TokenCredential credential,
        Action<AzureDocumentIntelligenceOcrOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);

        var options = BuildOptions(configure);
        return builder.UseDocumentOcrEngine(sp => new AzureDocumentIntelligenceOcrEngine(
            endpoint,
            credential,
            options,
            sp.GetService<ICostLedger>(),
            sp.GetService<ILogger<AzureDocumentIntelligenceOcrEngine>>()));
    }

    private static AzureDocumentIntelligenceOcrOptions BuildOptions(
        Action<AzureDocumentIntelligenceOcrOptions>? configure)
    {
        var options = new AzureDocumentIntelligenceOcrOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelId, nameof(configure));
        return options;
    }
}
