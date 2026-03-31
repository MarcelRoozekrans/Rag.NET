using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.Qdrant;

public static class QdrantBuilderExtensions
{
    public static TBuilder UseQdrant<TBuilder>(
        this TBuilder builder,
        string host,
        int port,
        string collectionName,
        int vectorDimensions = 1536)
        where TBuilder : IRagBuilder
    {
        var store = new QdrantVectorStore(host, port, collectionName, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
