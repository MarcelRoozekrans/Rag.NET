using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Pinecone;

public static class PineconeBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="PineconeVectorStore"/> as a single singleton serving
    /// <see cref="IVectorStore"/> and <see cref="ICollectionManageable"/>. With
    /// <see cref="PineconeOptions.EnableSparseVectors"/> set via <paramref name="configure"/>,
    /// the same singleton is a <see cref="PineconeSparseVectorStore"/> additionally serving
    /// <see cref="ISparseSearchable"/> (pair with <c>UseSpladeEncoder</c>; the index must
    /// use the dotproduct metric).
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="apiKey">
    /// Pinecone API key. Pinecone Local ignores it but still requires one — pass any
    /// placeholder there.
    /// </param>
    /// <param name="indexName">
    /// Serverless index to store chunks in — 1–45 characters of lowercase letters,
    /// digits, or <c>-</c>, starting and ending with a letter or digit. Validation
    /// matches Pinecone's documented rules; the server remains authoritative.
    /// </param>
    /// <param name="vectorDimensions">Dense embedding dimensions, used when creating the index.</param>
    /// <param name="configure">
    /// Optional callback to set <see cref="PineconeOptions.Namespace"/>,
    /// <see cref="PineconeOptions.EnableSparseVectors"/>,
    /// <see cref="PineconeOptions.Endpoint"/> (Pinecone Local), or the serverless
    /// cloud/region defaults.
    /// </param>
    public static TBuilder UsePinecone<TBuilder>(
        this TBuilder builder,
        string apiKey,
        string indexName,
        int vectorDimensions = 1536,
        Action<PineconeOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("The Pinecone API key must not be empty.", nameof(apiKey));
        ValidateIndexName(indexName, nameof(indexName));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);

        var options = new PineconeOptions
        {
            ApiKey = apiKey,
            IndexName = indexName,
            VectorDimensions = vectorDimensions,
        };
        configure?.Invoke(options);
        ValidateConfigured(options, nameof(configure));

        PineconeVectorStore store = options.EnableSparseVectors
            ? new PineconeSparseVectorStore(options)
            : new PineconeVectorStore(options);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        if (store is ISparseSearchable sparseSearchable)
            builder.Services.AddSingleton(sparseSearchable);
        return builder;
    }

    /// <summary>Re-validates whatever the <c>configure</c> callback may have changed.</summary>
    private static void ValidateConfigured(PineconeOptions options, string paramName)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("PineconeOptions.ApiKey must not be empty.", paramName);
        ValidateIndexName(options.IndexName, paramName);
        if (options.VectorDimensions <= 0)
            throw new ArgumentException("PineconeOptions.VectorDimensions must be positive.", paramName);
        if (options.Namespace is not null && string.IsNullOrWhiteSpace(options.Namespace))
            throw new ArgumentException("PineconeOptions.Namespace must not be whitespace.", paramName);
        if (string.IsNullOrWhiteSpace(options.Region))
            throw new ArgumentException("PineconeOptions.Region must not be empty.", paramName);
        if (options.IndexReadyTimeout <= TimeSpan.Zero)
            throw new ArgumentException("PineconeOptions.IndexReadyTimeout must be positive.", paramName);
    }

    private static void ValidateIndexName(string indexName, string paramName)
    {
        if (string.IsNullOrWhiteSpace(indexName) || !IsValidIndexName(indexName))
        {
            throw new ArgumentException(
                $"'{indexName}' is not a valid Pinecone index name. Names must be 1-45 " +
                "characters of lowercase letters, digits, or '-', starting and ending " +
                "with a letter or digit.",
                paramName);
        }
    }

    /// <summary>Matches Pinecone's documented naming rules; the server remains authoritative.</summary>
    private static bool IsValidIndexName(string indexName)
    {
        if (indexName.Length is < 1 or > 45)
            return false;
        if (!char.IsAsciiLetterLower(indexName[0]) && !char.IsAsciiDigit(indexName[0]))
            return false;
        if (!char.IsAsciiLetterLower(indexName[^1]) && !char.IsAsciiDigit(indexName[^1]))
            return false;

        foreach (var c in indexName)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                return false;
        }

        return true;
    }
}
