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
        int vectorDimensions = 1536)
        where TBuilder : IRagBuilder
    {
        var store = new AzureAISearchVectorStore(endpoint, indexName, credential, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<IHybridSearchable>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
