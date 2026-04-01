using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Chunking.Templates;

public static class RagBuilderExtensions
{
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
        builder.Services.AddSingleton<QAPairsDocumentParser>();
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
        builder.Services.AddSingleton<EmailDocumentParser>();
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
