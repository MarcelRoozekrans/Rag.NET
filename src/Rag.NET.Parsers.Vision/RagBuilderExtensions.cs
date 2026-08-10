using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;

namespace Rag.NET.Parsers.Vision;

public static class RagBuilderExtensions
{
    /// <summary>
    /// The call named in a <see cref="ParserClaim"/>, and therefore in the startup error a
    /// content-type conflict produces.
    /// </summary>
    private const string ImageRegistrationMethod = "UseImageDescription()";

    /// <inheritdoc cref="ImageRegistrationMethod"/>
    private const string VideoRegistrationMethod = "UseVideoDescription()";

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
                sp.GetRequiredService<ILogger<ImageDocumentParser>>()));
        builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<ImageDocumentParser>());
        builder.Services.AddSingleton<ImageChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<ImageChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<ImageChunkingStrategy>());

        // Declared here rather than left to AddParser<TParser>(): the parser is registered
        // through a factory lambda above (it needs opts.ChatClient and the resolved
        // ImageChunkingStrategy pairing), which does not route through AddParser and therefore
        // does not trigger its automatic claim declaration. This was Phase 4.2's one genuine
        // registration-site oversight — Archive, Email and Templates all declare claims from the
        // same AddSingleton<IDocumentParser> factory shape, and Vision alone did not. Read from
        // ImageDocumentParser.ContentTypes rather than repeated here, so the two cannot drift.
        foreach (var contentType in ImageDocumentParser.ContentTypes)
        {
            builder.Services.AddSingleton(ParserClaim.For<ImageDocumentParser>(
                contentType, ImageRegistrationMethod));
        }

        return builder;
    }

    public static TBuilder UseVideoDescription<TBuilder>(
        this TBuilder builder, Action<VideoDescriptionOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new VideoDescriptionOptions();
        configure?.Invoke(opts);
        if (opts.SceneChangeThreshold is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"{nameof(VideoDescriptionOptions)}.{nameof(VideoDescriptionOptions.SceneChangeThreshold)} " +
                $"({opts.SceneChangeThreshold.ToString(CultureInfo.InvariantCulture)}) must be within 0.0–1.0.");
        }
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<VideoDocumentParser>(sp =>
            new VideoDocumentParser(
                opts.ChatClient ?? sp.GetRequiredService<IChatClient>(),
                opts,
                sp.GetRequiredService<ILogger<VideoDocumentParser>>()));
        builder.Services.AddSingleton<IDocumentParser>(sp => sp.GetRequiredService<VideoDocumentParser>());
        builder.Services.AddSingleton<VideoChunkingStrategy>();
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<VideoChunkingStrategy>());
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<VideoChunkingStrategy>());

        // See UseImageDescription's remarks: same factory-lambda shape, same reason it must
        // declare its claims by hand instead of through AddParser<TParser>().
        foreach (var contentType in VideoDocumentParser.ContentTypes)
        {
            builder.Services.AddSingleton(ParserClaim.For<VideoDocumentParser>(
                contentType, VideoRegistrationMethod));
        }

        return builder;
    }
}
