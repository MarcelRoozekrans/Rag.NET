using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Vision;

public static class RagBuilderExtensions
{
    public static TBuilder UseImageDescription<TBuilder>(
        this TBuilder builder, Action<ImageDescriptionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new ImageDescriptionOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<ImageDocumentParser>(sp =>
            new ImageDocumentParser(
                opts.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                opts,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<ImageDocumentParser>>()));
        builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<ImageDocumentParser>());
        builder.Services.AddSingleton<ImageChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<ImageChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<ImageChunkingStrategy>());
        return builder;
    }

    public static TBuilder UseVideoDescription<TBuilder>(
        this TBuilder builder, Action<VideoDescriptionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new VideoDescriptionOptions();
        configure?.Invoke(opts);
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<VideoDocumentParser>(sp =>
            new VideoDocumentParser(
                opts.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                opts,
                sp.GetService<Microsoft.Extensions.Logging.ILogger<VideoDocumentParser>>()));
        builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<VideoDocumentParser>());
        builder.Services.AddSingleton<VideoChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<VideoChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<VideoChunkingStrategy>());
        return builder;
    }
}
