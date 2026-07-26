using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Chroma;

public static class ChromaBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="ChromaVectorStore"/> as a single singleton serving
    /// <see cref="IVectorStore"/> and <see cref="ICollectionManageable"/>. Chroma is the
    /// lightweight dense-only store — no hybrid or sparse search (use Qdrant or Weaviate
    /// for those).
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">Chroma HTTP endpoint, e.g. <c>http://localhost:8000</c>.</param>
    /// <param name="collectionName">
    /// Chroma collection to store chunks in — 3–512 characters of letters, digits,
    /// <c>.</c>, <c>_</c>, or <c>-</c>, starting and ending with a letter or digit.
    /// </param>
    /// <param name="configure">
    /// Optional callback to set <see cref="ChromaOptions.Tenant"/>,
    /// <see cref="ChromaOptions.Database"/>, or <see cref="ChromaOptions.ApiKey"/>.
    /// </param>
    public static TBuilder UseChroma<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        string collectionName,
        Action<ChromaOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateCollectionName(collectionName, nameof(collectionName));

        var options = new ChromaOptions
        {
            Endpoint = endpoint,
            CollectionName = collectionName,
        };
        configure?.Invoke(options);
        ValidateConfigured(options, nameof(configure));

        var store = new ChromaVectorStore(options);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }

    /// <summary>Re-validates whatever the <c>configure</c> callback may have changed.</summary>
    private static void ValidateConfigured(ChromaOptions options, string paramName)
    {
        if (options.Endpoint is null)
            throw new ArgumentException("ChromaOptions.Endpoint must not be null.", paramName);
        ValidateCollectionName(options.CollectionName, paramName);
        if (string.IsNullOrWhiteSpace(options.Tenant))
            throw new ArgumentException("ChromaOptions.Tenant must not be empty.", paramName);
        if (string.IsNullOrWhiteSpace(options.Database))
            throw new ArgumentException("ChromaOptions.Database must not be empty.", paramName);
    }

    private static void ValidateCollectionName(string collectionName, string paramName)
    {
        if (string.IsNullOrWhiteSpace(collectionName) || !IsValidCollectionName(collectionName))
        {
            throw new ArgumentException(
                $"'{collectionName}' is not a valid Chroma collection name. Names must be " +
                "3-512 characters of letters, digits, '.', '_', or '-', starting and " +
                "ending with a letter or digit.",
                paramName);
        }
    }

    private static bool IsValidCollectionName(string collectionName)
    {
        if (collectionName.Length is < 3 or > 512)
            return false;
        if (!char.IsAsciiLetterOrDigit(collectionName[0])
            || !char.IsAsciiLetterOrDigit(collectionName[^1]))
        {
            return false;
        }

        foreach (var c in collectionName)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }
}
