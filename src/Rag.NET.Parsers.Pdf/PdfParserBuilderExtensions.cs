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
}
