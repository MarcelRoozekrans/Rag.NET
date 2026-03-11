using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.PgVector;

public static class PgVectorBuilderExtensions
{
    public static RagBuilder UsePgVector(
        this RagBuilder builder,
        string connectionString,
        int vectorDimensions = 1536)
    {
        var store = new PgVectorStore(connectionString, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
