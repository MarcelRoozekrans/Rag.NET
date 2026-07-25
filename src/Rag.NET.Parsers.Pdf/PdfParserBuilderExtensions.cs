using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Pdf;

public static class PdfParserBuilderExtensions
{
    public static TBuilder AddPdfParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<PdfDocumentParser>();
        return builder;
    }

    public static TBuilder AddPdfParser<TBuilder>(this TBuilder builder, Action<PdfParserOptions>? configure)
        where TBuilder : IRagBuilder
    {
        var options = new PdfParserOptions();
        configure?.Invoke(options);
        ValidateOptions(options, nameof(configure));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IDocumentParser>(sp =>
            new PdfDocumentParser(options, sp.GetService<ILogger<PdfDocumentParser>>()));
        return builder;
    }

    private static void ValidateOptions(PdfParserOptions options, string paramName)
    {
        ThrowIfLessThanOne(options.MinTableRows, nameof(PdfParserOptions.MinTableRows), paramName);
        ThrowIfLessThanOne(options.MinTableColumns, nameof(PdfParserOptions.MinTableColumns), paramName);
        if (options.OcrMinCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(paramName, options.OcrMinCharacters,
                $"{nameof(PdfParserOptions)}.{nameof(PdfParserOptions.OcrMinCharacters)} must not be negative.");
        }

        if (options.UseOcrFallback)
        {
            ThrowIfNullOrWhiteSpace(options.TessDataPath, nameof(PdfParserOptions.TessDataPath), paramName);
            ThrowIfNullOrWhiteSpace(options.OcrLanguage, nameof(PdfParserOptions.OcrLanguage), paramName);
        }
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
