using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Epub;

public static class EpubParserBuilderExtensions
{
    public static TBuilder AddEpubParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.AddSingleton<HtmlDocumentParser>();
        builder.AddParser<EpubDocumentParser>();
        return builder;
    }
}
