using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Qdrant;

public static class QdrantBuilderExtensions
{
    public static RagBuilder UseQdrant(
        this RagBuilder builder,
        string host,
        int port,
        string collectionName,
        int vectorDimensions = 1536)
    {
        var store = new QdrantVectorStore(host, port, collectionName, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
