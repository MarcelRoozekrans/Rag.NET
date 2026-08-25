using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Html;

public static class HtmlParserBuilderExtensions
{
    /// <summary>Registers the HTML parser.</summary>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="configure">
    /// Optional options callback — notably <see cref="HtmlParserOptions.HrefHandling"/>, which
    /// decides whether a link's URL is appended to its text, dropped, or resolved to an absolute
    /// URL first (#371). Omitting it keeps the behaviour this parser had before options existed.
    /// </param>
    public static TBuilder AddHtmlParser<TBuilder>(this TBuilder builder, Action<HtmlParserOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HtmlParserOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.AddParser<HtmlDocumentParser>();
        return builder;
    }
}
