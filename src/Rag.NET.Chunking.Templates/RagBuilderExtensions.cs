using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.Templates;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Full type name of core's <c>Rag.NET.Parsers.CsvDocumentParser</c>. This project references
    /// neither <c>Rag.NET</c> nor <c>Rag.NET.Parsers.Office</c> — see
    /// <see cref="QAPairsExcelDocumentParserTypeName"/> — so the type this claim deliberately
    /// overrides is named as a string rather than passed as a <see cref="Type"/> to
    /// <c>AddParser&lt;TParser&gt;(replacesTypeNames:)</c>. <c>CsvDocumentParser</c> carries no
    /// <c>[Singleton]</c> attribute and nothing registers it by default, so this override is a
    /// no-op unless a caller has explicitly added it themselves.
    /// </summary>
    private const string QAPairsCsvDocumentParserTypeName = "Rag.NET.Parsers.CsvDocumentParser";

    /// <summary>
    /// Full type name of <c>Rag.NET.Parsers.Excel.ExcelDocumentParser</c>, from the optional
    /// <c>Rag.NET.Parsers.Office</c> package. <c>Rag.NET.Chunking.Templates</c> must not take a
    /// compile-time dependency on Office to say this — that package may not even be installed — so
    /// the override is named rather than typed, the same reasoning as
    /// <see cref="QAPairsCsvDocumentParserTypeName"/>. Replacing a type that was never registered
    /// removes nothing and is not an error, which is exactly the shape "Office isn't installed"
    /// needs.
    /// </summary>
    private const string QAPairsExcelDocumentParserTypeName = "Rag.NET.Parsers.Excel.ExcelDocumentParser";

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
    /// <para>
    /// After Phase 3.11 this parser claims only <c>text/csv</c> and the two spreadsheet types, and
    /// two of those three genuinely overlap other packages: core's <c>CsvDocumentParser</c> (if a
    /// caller has explicitly added it) and <c>Rag.NET.Parsers.Office</c>'s <c>ExcelDocumentParser</c>
    /// (if that package is installed). Both overlaps are deliberate overrides declared via
    /// <c>AddParser&lt;TParser&gt;(replacesTypeNames:)</c> rather than left to collide — a caller who
    /// asked for QA-pairs chunking wants this parser to win. <b>The behaviour change this is:</b>
    /// enabling QA-pairs chunking now means plain CSVs (and Excel workbooks, with Office installed)
    /// are parsed as QA pairs, because that is what the override says. Neither replacement is an
    /// error when the parser it names was never registered — Office may not be installed, and
    /// <c>CsvDocumentParser</c> is not registered by default.
    /// </para>
    /// <para>
    /// The opt-out is here because the conflict guard still fires on any duplicated claim this
    /// override does not name — a third-party CSV parser, for instance — and an escape hatch present
    /// on one bundling registration and absent from its twin is a trap of its own: the user who hits
    /// that conflict finds the error naming an opt-out the sibling call does not have. See
    /// <see cref="UseEmailChunking"/> for why it is a parameter rather than a property on
    /// <see cref="QAPairsChunkingOptions"/>.
    /// </para>
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
            builder.AddParser<QAPairsDocumentParser>(replacesTypeNames:
            [
                QAPairsCsvDocumentParserTypeName,
                QAPairsExcelDocumentParserTypeName,
            ]);
            builder.Services.AddSingleton(ParserClaim.For<QAPairsDocumentParser>(
                "text/csv", "UseQAPairsChunking()", QAPairsParserOptOut,
                replacesTypeName: QAPairsCsvDocumentParserTypeName));
            builder.Services.AddSingleton(ParserClaim.For<QAPairsDocumentParser>(
                "application/vnd.ms-excel", "UseQAPairsChunking()", QAPairsParserOptOut));
            builder.Services.AddSingleton(ParserClaim.For<QAPairsDocumentParser>(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "UseQAPairsChunking()", QAPairsParserOptOut,
                replacesTypeName: QAPairsExcelDocumentParserTypeName));
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
