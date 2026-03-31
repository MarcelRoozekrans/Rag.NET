using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;

namespace Rag.NET.PgVector;

public static class PgVectorBuilderExtensions
{
    public static TBuilder UsePgVector<TBuilder>(
        this TBuilder builder,
        string connectionString,
        int vectorDimensions = 1536)
        where TBuilder : IRagBuilder
    {
        var store = new PgVectorStore(connectionString, vectorDimensions);
        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<ICollectionManageable>(store);
        return builder;
    }
}
