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

    /// <summary>
    /// Registers <see cref="PropositionChunkingStrategy"/> as both <see cref="IChunkingStrategy"/>
    /// and <see cref="IDocumentChunkingStrategy"/>, resolving to the same singleton instance.
    /// The strategy sends token-bounded passages to the DI-registered
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/> (or
    /// <see cref="PropositionChunkingOptions.ChatClient"/> when set) and emits one chunk per
    /// extracted proposition; on LLM or parse failure the passage is emitted as a fallback chunk.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="PropositionChunkingOptions"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="PropositionChunkingOptions.MaxPassageTokens"/> or
    /// <see cref="PropositionChunkingOptions.MaxPropositionsPerPassage"/> is not positive.
    /// </exception>
    public static TBuilder UsePropositionChunking<TBuilder>(this TBuilder builder, Action<PropositionChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new PropositionChunkingOptions();
        configure?.Invoke(opts);

        if (opts.MaxPassageTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"MaxPassageTokens ({opts.MaxPassageTokens}) must be greater than zero.");
        }

        if (opts.MaxPropositionsPerPassage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure),
                $"MaxPropositionsPerPassage ({opts.MaxPropositionsPerPassage}) must be greater than zero.");
        }

        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<PropositionChunkingStrategy>();
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<PropositionChunkingStrategy>());
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<PropositionChunkingStrategy>());
        return builder;
    }

    /// <summary>
    /// Registers <see cref="LateChunkingStrategy"/> as both <see cref="IChunkingStrategy"/>
    /// and <see cref="IDocumentChunkingStrategy"/>, resolving to the same singleton instance.
    /// Each section is embedded in one pass by the DI-registered
    /// <see cref="ITokenEmbeddingGenerator"/> (or <see cref="LateChunkingOptions.Generator"/>
    /// when set), overlapping token windows are cut, and each chunk carries the L2-normalized
    /// mean of its window's token vectors as a precomputed embedding; on generator failure,
    /// unembedded cl100k token-window chunks are emitted instead.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="configure">Configures the <see cref="LateChunkingOptions"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown at registration when <see cref="LateChunkingOptions.WindowSizeTokens"/> is not
    /// positive, or <see cref="LateChunkingOptions.OverlapTokens"/> is negative or not smaller
    /// than <see cref="LateChunkingOptions.WindowSizeTokens"/>.
    /// </exception>
    public static TBuilder UseLateChunking<TBuilder>(this TBuilder builder, Action<LateChunkingOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var opts = new LateChunkingOptions();
        configure?.Invoke(opts);
        LateChunkingStrategy.ValidateOptions(opts); // same rules as the ctor — fail at registration

        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<LateChunkingStrategy>(sp => new LateChunkingStrategy(
            opts.Generator ?? sp.GetRequiredService<ITokenEmbeddingGenerator>(),
            opts,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<LateChunkingStrategy>>()));
        builder.Services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<LateChunkingStrategy>());
        builder.Services.AddSingleton<IDocumentChunkingStrategy>(sp => sp.GetRequiredService<LateChunkingStrategy>());
        return builder;
    }
}
