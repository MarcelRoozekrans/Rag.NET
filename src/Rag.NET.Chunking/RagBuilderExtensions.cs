using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public static class RagBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="HierarchicalMergerChunkingStrategy"/> which merges document sections
    /// into heading-subtree chunks. Each chunk covers one heading and all body text under it
    /// down to <paramref name="options"/>.<see cref="HierarchicalMergerOptions.MaxDepth"/>.
    /// Uses <see cref="DocumentSection.HeadingLevel"/> when available; falls back to
    /// <see cref="HierarchicalMergerOptions.HeadingPatterns"/> for formats without heading metadata.
    /// </summary>
    public static TBuilder UseHierarchicalMerging<TBuilder>(this TBuilder builder, HierarchicalMergerOptions? options = null)
        where TBuilder : IRagBuilder
    {
        var opts = options ?? new HierarchicalMergerOptions();
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IChunkingStrategy>(_ => new HierarchicalMergerChunkingStrategy(opts));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="CodeChunkingStrategy"/> as <see cref="IChunkingStrategy"/>.
    /// Splits code files at language-appropriate boundaries (class, function, method level) using
    /// per-language separator hierarchies. Language is auto-detected from the file extension in
    /// <c>DocumentSection.DocumentId.Value</c> when <see cref="CodeChunkingOptions.Language"/> is null.
    /// </summary>
    /// <param name="options">
    /// Optional options. Set <see cref="CodeChunkingOptions.Language"/> to override extension detection.
    /// Throws <see cref="ArgumentException"/> immediately for unrecognised language values.
    /// </param>
    public static TBuilder UseCodeChunking<TBuilder>(this TBuilder builder, CodeChunkingOptions? options = null)
        where TBuilder : IRagBuilder
    {
        var opts     = options ?? new CodeChunkingOptions();
        var strategy = new CodeChunkingStrategy(opts); // validates Language immediately
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<IChunkingStrategy>(_ => strategy);
        return builder;
    }
}
