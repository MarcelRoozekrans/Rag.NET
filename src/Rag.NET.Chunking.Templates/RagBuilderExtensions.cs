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

    /// <summary>The content type <see cref="EmailTemplateDocumentParser.CanParse"/> accepts.</summary>
    private const string EmailContentType = "message/rfc822";

    /// <summary>
    /// Carried on the <see cref="ParserClaim"/>s these calls declare so the startup error a
    /// content-type conflict produces can name a way out that keeps the chunking strategy. Both
    /// calls register a parser <i>and</i> a strategy, and it is only ever the parser that
    /// collides. Quoted verbatim into the message, so these must stay pasteable call syntax.
    /// </summary>
    private const string EmailParserOptOut = "UseEmailChunking(registerParser: false)";

    /// <inheritdoc cref="EmailParserOptOut"/>
    private const string QAPairsParserOptOut = "UseQAPairsChunking(registerParser: false)";

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

    /// <param name="registerParser">
    /// Whether to register <see cref="QAPairsDocumentParser"/> and its
    /// <see cref="ParserClaim"/> alongside the chunking strategy. Defaults to
    /// <see langword="true"/>; pass <see langword="false"/> to take the strategy alone.
    /// </param>
    /// <remarks>
    /// Nothing ships in this repository that collides with this parser's claims today — after
    /// Phase 3.11 it claims only <c>text/csv</c> and the two spreadsheet types. The opt-out is here
    /// because the conflict guard fires on <i>any</i> duplicated claim, including one a third-party
    /// CSV parser introduces, and an escape hatch present on one bundling registration and absent
    /// from its twin is a trap of its own: the user who hits the second conflict finds the error
    /// naming an opt-out the sibling call does not have. See <see cref="UseEmailChunking"/> for why
    /// it is a parameter rather than a property on <see cref="QAPairsChunkingOptions"/>.
    /// </remarks>
    public static TBuilder UseQAPairsChunking<TBuilder>(
        this TBuilder builder,
        Action<QAPairsChunkingOptions>? configure = null,
        bool registerParser = true)
        where TBuilder : IRagBuilder
    {
        var opts = new QAPairsChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        if (registerParser)
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

    /// <param name="registerParser">
    /// Whether to register <see cref="EmailTemplateDocumentParser"/> and its
    /// <see cref="ParserClaim"/> alongside the chunking strategy. Defaults to
    /// <see langword="true"/>; pass <see langword="false"/> to take the strategy alone.
    /// </param>
    /// <remarks>
    /// <para>
    /// The escape hatch exists because this call registers two independent things — a parser and a
    /// chunking strategy — and only the parser can collide. <see cref="EmailTemplateDocumentParser"/>
    /// and <c>Rag.NET.Parsers.Email</c>'s parser both claim <c>message/rfc822</c>, which Phase 3.11
    /// made a startup error; without a way to drop just the parser, that error told the user to
    /// "register only one of them" while offering no way to keep the email-shaped chunking and let
    /// the other package parse.
    /// </para>
    /// <para>
    /// A parameter rather than a property on the options object. The options types configure
    /// <i>chunking</i>, and their only consumers are the parsers — <see cref="EmailChunkingStrategy"/>
    /// and <see cref="QAPairsChunkingStrategy"/> take no options at all. A registration switch
    /// living there meant <c>UseEmailChunking(o => { o.IncludeHeaders = false; o.RegisterParser =
    /// false; })</c> compiled, ran, threw nothing and silently discarded <c>IncludeHeaders</c>,
    /// because dropping the parser dropped its only reader. On the call it is visible where the
    /// mutual exclusivity actually is.
    /// </para>
    /// <para>
    /// The chunking strategy is unaffected either way: it consumes
    /// <see cref="Models.DocumentSection"/>s and does not care which parser produced them.
    /// </para>
    /// </remarks>
    public static TBuilder UseEmailChunking<TBuilder>(
        this TBuilder builder,
        Action<EmailChunkingOptions>? configure = null,
        bool registerParser = true)
        where TBuilder : IRagBuilder
    {
        var opts = new EmailChunkingOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        if (registerParser)
        {
            builder.Services.AddSingleton<EmailTemplateDocumentParser>();
            builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<EmailTemplateDocumentParser>());
            builder.Services.AddSingleton(ParserClaim.For<EmailTemplateDocumentParser>(
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
