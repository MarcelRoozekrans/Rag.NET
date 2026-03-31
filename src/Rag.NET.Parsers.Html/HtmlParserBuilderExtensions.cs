using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Html;

public static class HtmlParserBuilderExtensions
{
    public static TBuilder AddHtmlParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.AddParser<HtmlDocumentParser>();
        return builder;
    }
}
