using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Pdf.Ocr;

namespace Rag.NET.Parsers.Pdf;

public static class PdfParserBuilderExtensions
{
    private const string BothEnginesMessage =
        "Two OCR engines are configured for the PDF parser: the per-image Tesseract fallback " +
        "(PdfParserOptions.UseOcrFallback) and a document-level engine (IDocumentOcrEngine). " +
        "The parser uses one engine, and which one it would be is not something to leave to a " +
        "silent precedence rule — choose either UseOcrFallback = true or UseDocumentOcrEngine(...).";

    /// <summary>Registers the PDF parser with default <see cref="PdfParserOptions"/>.</summary>
    public static TBuilder AddPdfParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<PdfDocumentParser>();
        return builder;
    }

    /// <summary>
    /// Registers the PDF parser with configured <see cref="PdfParserOptions"/>.
    /// Calling any <c>AddPdfParser</c> overload more than once registers multiple parser
    /// instances — the pipeline uses the first whose <c>CanParse</c> matches (first
    /// registration wins at parse time), while the <see cref="PdfParserOptions"/> singleton
    /// resolved from DI is the most recently registered one (last wins); each parser
    /// instance keeps the options it was configured with.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="PdfDocumentParser"/> through a factory lambda rather than
    /// <c>AddParser&lt;TParser&gt;()</c>, because it needs to close over <paramref name="configure"/>'s
    /// resolved <see cref="PdfParserOptions"/> and the optional <see cref="IDocumentOcrEngine"/>. That
    /// bypasses <c>AddParser</c>'s automatic <see cref="ParserClaim"/> declaration, so this declares
    /// the same claims by hand from <see cref="PdfDocumentParser.ContentTypes"/> — the same source
    /// <c>AddParser&lt;PdfDocumentParser&gt;()</c> would have read, kept in sync because both read the
    /// one static list rather than each naming <c>application/pdf</c> separately.
    /// </remarks>
    public static TBuilder AddPdfParser<TBuilder>(this TBuilder builder, Action<PdfParserOptions>? configure)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new PdfParserOptions();
        configure?.Invoke(options);
        ValidateOptions(options, nameof(configure), builder.Services);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IDocumentParser>(sp =>
            new PdfDocumentParser(
                options,
                sp.GetService<ILogger<PdfDocumentParser>>(),
                sp.GetService<IDocumentOcrEngine>()));

        foreach (var contentType in PdfDocumentParser.ContentTypes)
        {
            builder.Services.AddSingleton(ParserClaim.For<PdfDocumentParser>(
                contentType, "AddPdfParser(configure)"));
        }

        return builder;
    }

    /// <summary>
    /// Registers a document-level OCR engine — one that takes the whole PDF and returns every
    /// page from a single call. Registering an engine <b>is</b> the opt-in: the parser routes
    /// sub-threshold pages through it without <see cref="PdfParserOptions.UseOcrFallback"/>,
    /// which stays the switch for the per-image Tesseract fallback.
    /// <para>
    /// Configuring both engines throws here (or from <c>AddPdfParser</c>, whichever call comes
    /// second) rather than silently preferring one.
    /// </para>
    /// <para>
    /// <b>Memory profile.</b> Registering an engine changes how the parser reads: it buffers
    /// each PDF into memory in full before parsing, because PdfPig and the engine both need
    /// to read the document and PdfPig consumes its stream lazily. This happens for every PDF
    /// on this path, not only the ones that turn out to need OCR, so a parser that used to
    /// stream now holds whole files resident — large ones on the large object heap —
    /// multiplied by however many documents ingest in parallel. Size the host accordingly, or
    /// keep the engine off the parser that handles your largest inputs.
    /// </para>
    /// <para>
    /// Spend is bounded by <see cref="PdfParserOptions.MaxOcrPages"/>: providers bill every
    /// page of the submitted document, not just the pages that needed OCR.
    /// </para>
    /// </summary>
    public static TBuilder UseDocumentOcrEngine<TBuilder>(this TBuilder builder, IDocumentOcrEngine engine)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(engine);
        ThrowIfTesseractFallbackConfigured(builder.Services);
        builder.Services.AddSingleton(engine);
        return builder;
    }

    /// <inheritdoc cref="UseDocumentOcrEngine{TBuilder}(TBuilder, IDocumentOcrEngine)"/>
    public static TBuilder UseDocumentOcrEngine<TBuilder>(
        this TBuilder builder, Func<IServiceProvider, IDocumentOcrEngine> engineFactory)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(engineFactory);
        ThrowIfTesseractFallbackConfigured(builder.Services);
        builder.Services.AddSingleton(engineFactory);
        return builder;
    }

    private static void ValidateOptions(PdfParserOptions options, string paramName, IServiceCollection services)
    {
        ThrowIfLessThanOne(options.MinTableRows, nameof(PdfParserOptions.MinTableRows), paramName);
        ThrowIfLessThanOne(options.MinTableColumns, nameof(PdfParserOptions.MinTableColumns), paramName);
        ThrowIfLessThanOne(options.MaxOcrPages, nameof(PdfParserOptions.MaxOcrPages), paramName);
        if (options.OcrMinCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, options.OcrMinCharacters,
                $"{nameof(PdfParserOptions)}.{nameof(PdfParserOptions.OcrMinCharacters)} must not be negative.");
        }

        if (!options.UseOcrFallback)
        {
            return;
        }

        // Engine-conditional: TessDataPath and OcrLanguage configure Tesseract, so they are
        // only required when Tesseract is the engine that will actually run. With a
        // document-level engine registered they are meaningless — and that combination is an
        // error in its own right, reported before the spurious Tesseract complaint.
        if (HasDocumentOcrEngine(services))
        {
            throw new InvalidOperationException(BothEnginesMessage);
        }

        ThrowIfNullOrWhiteSpace(options.TessDataPath, nameof(PdfParserOptions.TessDataPath), paramName);
        ThrowIfNullOrWhiteSpace(options.OcrLanguage, nameof(PdfParserOptions.OcrLanguage), paramName);
    }

    private static void ThrowIfTesseractFallbackConfigured(IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(PdfParserOptions)
                && services[i].ImplementationInstance is PdfParserOptions options
                && options.UseOcrFallback)
            {
                throw new InvalidOperationException(BothEnginesMessage);
            }
        }
    }

    private static bool HasDocumentOcrEngine(IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IDocumentOcrEngine))
            {
                return true;
            }
        }

        return false;
    }

    private static void ThrowIfLessThanOne(int value, string propertyName, string paramName)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(paramName, value,
                $"{nameof(PdfParserOptions)}.{propertyName} must be at least 1.");
        }
    }

    private static void ThrowIfNullOrWhiteSpace(string value, string propertyName, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{nameof(PdfParserOptions)}.{propertyName} must be a non-empty string when " +
                $"{nameof(PdfParserOptions.UseOcrFallback)} is enabled.", paramName);
        }
    }
}
