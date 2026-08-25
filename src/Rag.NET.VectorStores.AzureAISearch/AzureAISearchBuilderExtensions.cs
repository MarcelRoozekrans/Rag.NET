using Azure;
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.AzureAISearch;

public static class AzureAISearchBuilderExtensions
{
    public static TBuilder UseAzureAISearch<TBuilder>(
        this TBuilder builder,
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        int vectorDimensions = 1536,
        Action<AzureAISearchOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        var options = new AzureAISearchOptions();
        configure?.Invoke(options);
        ValidateConfigured(options, nameof(configure));

        var store = new AzureAISearchVectorStore(
            endpoint, indexName, credential, vectorDimensions, clientOptions: null, options);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<IHybridSearchable>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }

    /// <summary>Re-validates whatever the <c>configure</c> callback may have changed.</summary>
    /// <remarks>
    /// Eager, matching the sibling stores: a <c>k</c> of zero or less is rejected at registration
    /// rather than surfacing as an opaque Azure error on the first query. Leaving it
    /// <see langword="null"/> is valid and is the default — it omits the parameter so Azure applies
    /// its own documented default of 50.
    /// </remarks>
    /// <param name="options">The options after <c>configure</c> ran.</param>
    /// <param name="paramName">The callback's parameter name, for the exception.</param>
    private static void ValidateConfigured(AzureAISearchOptions options, string paramName)
    {
        if (options.KNearestNeighborsCount is { } k && k < 1)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                k,
                "KNearestNeighborsCount must be at least 1, or null to use Azure's own default of 50.");
        }
    }
}
