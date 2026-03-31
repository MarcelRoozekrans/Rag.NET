using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Excel;

public static class ExcelParserBuilderExtensions
{
    public static TBuilder AddExcelParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<ExcelDocumentParser>();
        return builder;
    }
}
