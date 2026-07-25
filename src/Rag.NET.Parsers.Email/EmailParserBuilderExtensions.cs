using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.Parsers.Html;

namespace Rag.NET.Parsers.Email;

public static class EmailParserBuilderExtensions
{
    /// <summary>
    /// Registers the EML (<see cref="EmailDocumentParser"/>) and MSG
    /// (<see cref="MsgDocumentParser"/>) parsers plus their shared
    /// <see cref="HtmlDocumentParser"/> dependency. Both parsers receive the parser
    /// collection through a deferred <see cref="LazyDocumentParsers"/> view — they are part
    /// of the collection they dispatch attachments to, so eager constructor injection of
    /// <c>IEnumerable&lt;IDocumentParser&gt;</c> would be a circular dependency.
    /// </summary>
    public static TBuilder AddEmailParser<TBuilder>(this TBuilder builder)
        where TBuilder : IRagBuilder
    {
        builder.Services.TryAddSingleton<HtmlDocumentParser>();
        builder.Services.AddSingleton<IDocumentParser>(sp => new EmailDocumentParser(
            new LazyDocumentParsers(sp),
            sp.GetRequiredService<HtmlDocumentParser>(),
            sp.GetService<ILogger<EmailDocumentParser>>()));
        builder.Services.AddSingleton<IDocumentParser>(sp => new MsgDocumentParser(
            new LazyDocumentParsers(sp),
            sp.GetRequiredService<HtmlDocumentParser>(),
            sp.GetService<ILogger<MsgDocumentParser>>()));
        return builder;
    }
}
