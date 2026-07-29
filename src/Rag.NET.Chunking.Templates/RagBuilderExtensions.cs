using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.Templates;

public static class RagBuilderExtensions
{
    /// <summary>
    /// The content types <see cref="QAPairsDocumentParser.CanParse"/> accepts, declared here as
    /// <see cref="ParserClaim"/>s so a second package claiming any of them is a startup error
    /// rather than registration-order roulette. Kept next to the registration on purpose: the two
    /// must agree, and nothing enforces that but proximity.
    /// </summary>
    private static readonly string[] QAPairsContentTypes =
    [
        "text/csv",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    ];

    /// <summary>The content type <see cref="EmailDocumentParser.CanParse"/> accepts.</summary>
    private const string EmailContentType = "message/rfc822";

    /// <summary>
    /// Carried on the <see cref="ParserClaim"/>s these calls declare so the startup error a
    /// content-type conflict produces can name a way out that keeps the chunking strategy. Both
    /// calls register a parser <i>and</i> a strategy, and it is only ever the parser that
    /// collides.
    /// </summary>
    private const string EmailParserOptOut = "UseEmailChunking(o => o.RegisterParser = false)";

    /// <inheritdoc cref="EmailParserOptOut"/>
    private const string QAPairsParserOptOut = "UseQAPairsChunking(o => o.RegisterParser = false)";

    public static TBuilder UseLegalChunking<TBuilder>(
        this TBuilder builder, Action<LegalChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new LegalChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<LegalChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<LegalChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<LegalChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseBookChunking<TBuilder>(
        this TBuilder builder, Action<BookChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new BookChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<BookChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<BookChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<BookChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseAcademicPaperChunking<TBuilder>(
        this TBuilder builder, Action<AcademicPaperChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new AcademicPaperChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<AcademicPaperChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<AcademicPaperChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<AcademicPaperChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseQAPairsChunking<TBuilder>(
        this TBuilder builder, Action<QAPairsChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new QAPairsChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        if (opts.RegisterParser)
        {
            builder.Services.AddSingleton<QAPairsDocumentParser>();
            builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<QAPairsDocumentParser>());
            foreach (var contentType in QAPairsContentTypes)
            {
                builder.Services.AddSingleton(ParserClaim.For<QAPairsDocumentParser>(
                    contentType, "UseQAPairsChunking()", QAPairsParserOptOut));
            }
        }

        builder.Services.AddSingleton<QAPairsChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<QAPairsChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseEmailChunking<TBuilder>(
        this TBuilder builder, Action<EmailChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new EmailChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        if (opts.RegisterParser)
        {
            builder.Services.AddSingleton<EmailDocumentParser>();
            builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<EmailDocumentParser>());
            builder.Services.AddSingleton(ParserClaim.For<EmailDocumentParser>(
                EmailContentType, "UseEmailChunking()", EmailParserOptOut));
        }

        builder.Services.AddSingleton<EmailChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<EmailChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<EmailChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseResumeChunking<TBuilder>(
        this TBuilder builder, Action<ResumeChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new ResumeChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<ResumeChunkingStrategy>(sp =>
            new ResumeChunkingStrategy(
                opts.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                opts,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<ResumeChunkingStrategy>>()));
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<ResumeChunkingStrategy>());
        return builder;
    }
}
