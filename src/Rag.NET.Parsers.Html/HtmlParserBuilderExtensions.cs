using Rag.NET.DependencyInjection;

namespace Rag.NET.Parsers.Html;

public static class HtmlParserBuilderExtensions
{
    public static RagBuilder AddHtmlParser(this RagBuilder builder)
    {
        builder.AddParser<HtmlDocumentParser>();
        return builder;
    }
}
