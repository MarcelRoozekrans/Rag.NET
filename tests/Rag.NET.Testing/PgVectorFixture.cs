using Testcontainers.PostgreSql;
using Xunit;

namespace Rag.NET.Testing;

public sealed class PgVectorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("ragnet_test")
        .WithUsername("postgres")
        .WithPassword("postgres_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await _container.StartAsync();

    public async ValueTask DisposeAsync() =>
        await _container.DisposeAsync();
}
