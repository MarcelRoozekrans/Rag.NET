using Microsoft.Extensions.AI;
using Rag.NET.Models.Options;

namespace Rag.NET.Ingestion;

/// <summary>
/// Resolves the identity string of the active embedding model for version stamping.
/// Resolution order: explicit <see cref="EmbeddingVersioningOptions.ModelId"/> override,
/// then the generator's <see cref="EmbeddingGeneratorMetadata"/>
/// (<c>"{ProviderName}/{DefaultModelId}"</c>), else <see langword="null"/> — versioning
/// is disabled rather than guessed.
/// </summary>
internal static class EmbeddingModelIdentity
{
    /// <summary>
    /// Returns the model identity, or <see langword="null"/> when it cannot be resolved.
    /// A metadata-derived identity requires a non-empty
    /// <see cref="EmbeddingGeneratorMetadata.DefaultModelId"/>; a provider name alone does
    /// not identify a model. The provider prefix is omitted when absent.
    /// </summary>
    internal static string? Resolve(
        IEmbeddingGenerator<string, Embedding<float>>? embedder,
        EmbeddingVersioningOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelId))
            return options.ModelId;

        var metadata = embedder?.GetService<EmbeddingGeneratorMetadata>();
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.DefaultModelId))
            return null;

        return string.IsNullOrWhiteSpace(metadata.ProviderName)
            ? metadata.DefaultModelId
            : $"{metadata.ProviderName}/{metadata.DefaultModelId}";
    }
}
