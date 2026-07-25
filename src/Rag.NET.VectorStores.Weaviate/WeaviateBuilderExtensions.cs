using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Weaviate;

public static class WeaviateBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="WeaviateVectorStore"/> as a single singleton serving
    /// <see cref="IVectorStore"/>, <see cref="IHybridSearchable"/> (native BM25+vector
    /// fusion), and <see cref="ICollectionManageable"/>.
    /// </summary>
    /// <param name="builder">The RAG builder.</param>
    /// <param name="endpoint">Weaviate HTTP endpoint, e.g. <c>http://localhost:8080</c>.</param>
    /// <param name="className">
    /// Weaviate class to store chunks in — a capital letter followed by letters, digits, or
    /// underscores (GraphQL naming rules).
    /// </param>
    /// <param name="vectorDimensions">Dense embedding dimensions (sanity-checked; Weaviate infers the actual dimensions from stored vectors).</param>
    /// <param name="configure">Optional callback to set <see cref="WeaviateOptions.ApiKey"/> or <see cref="WeaviateOptions.Tenant"/>.</param>
    public static TBuilder UseWeaviate<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        string className,
        int vectorDimensions = 1536,
        Action<WeaviateOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateClassName(className, nameof(className));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorDimensions);

        var options = new WeaviateOptions
        {
            Endpoint = endpoint,
            ClassName = className,
            VectorDimensions = vectorDimensions,
        };
        configure?.Invoke(options);
        ValidateConfigured(options, nameof(configure));

        var store = new WeaviateVectorStore(options);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<IHybridSearchable>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }

    /// <summary>Re-validates whatever the <c>configure</c> callback may have changed.</summary>
    private static void ValidateConfigured(WeaviateOptions options, string paramName)
    {
        if (options.Endpoint is null)
            throw new ArgumentException("WeaviateOptions.Endpoint must not be null.", paramName);
        ValidateClassName(options.ClassName, paramName);
        if (options.VectorDimensions <= 0)
            throw new ArgumentException("WeaviateOptions.VectorDimensions must be positive.", paramName);
        if (options.Tenant is not null && string.IsNullOrWhiteSpace(options.Tenant))
            throw new ArgumentException("WeaviateOptions.Tenant must not be whitespace.", paramName);
    }

    private static void ValidateClassName(string className, string paramName)
    {
        if (string.IsNullOrWhiteSpace(className) || !IsValidClassName(className))
        {
            throw new ArgumentException(
                $"'{className}' is not a valid Weaviate class name. Class names double as " +
                "GraphQL fields: they must start with a capital letter and contain only " +
                "letters, digits, or underscores.",
                paramName);
        }
    }

    private static bool IsValidClassName(string className)
    {
        if (!char.IsAsciiLetterUpper(className[0]))
            return false;

        for (var i = 1; i < className.Length; i++)
        {
            var c = className[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }
}
