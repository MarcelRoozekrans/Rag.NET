using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Pdf;

public static class PdfParserBuilderExtensions
{
    public static RagBuilder AddPdfParser(this RagBuilder builder)
    {
        builder.AddParser<PdfDocumentParser>();
        return builder;
    }
}
