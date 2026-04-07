using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public static class EmailParserBuilderExtensions
{
    public static TBuilder AddEmailParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.TryAddSingleton<HtmlDocumentParser>();
        builder.AddParser<EmailDocumentParser>();
        return builder;
    }
}
