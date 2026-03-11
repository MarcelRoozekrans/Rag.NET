using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Excel;

public static class ExcelParserBuilderExtensions
{
    public static RagBuilder AddExcelParser(this RagBuilder builder)
    {
        builder.AddParser<ExcelDocumentParser>();
        return builder;
    }
}
