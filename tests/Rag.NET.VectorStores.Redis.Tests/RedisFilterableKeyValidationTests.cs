using StackExchange.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// Declared filterable key names are validated at construction (#513 finding 7), because the name
/// goes into every filtered query unescaped: <c>MetadataFieldName</c> is raw concatenation, and a
/// key like <c>tenant-id</c> would pass <c>FT.CREATE</c> and the <c>FT.INFO</c> guard, then break
/// every filtered query at read time — DIALECT 2 reads <c>-</c> as NOT inside <c>@md_tenant-id:{…}</c>.
/// </summary>
/// <remarks>
/// No container: construction never touches Redis, so these run against an unconnected
/// multiplexer — <c>abortConnect=false</c> makes <see cref="ConnectionMultiplexer.Connect"/> return
/// immediately without requiring a reachable server.
/// </remarks>
public sealed class RedisFilterableKeyValidationTests
{
    private static IConnectionMultiplexer FakeConnection() =>
        ConnectionMultiplexer.Connect("127.0.0.1:1,abortConnect=false,connectTimeout=200,connectRetry=0");

    [Fact]
    public void ANullKeyThrows()
    {
        using var connection = FakeConnection();

        var error = Assert.Throws<ArgumentException>(
            () => new RedisVectorStore(connection, filterableMetadataKeys: [null!]));

        Assert.Contains("null", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWhitespaceKeyThrows()
    {
        using var connection = FakeConnection();

        var error = Assert.Throws<ArgumentException>(
            () => new RedisVectorStore(connection, filterableMetadataKeys: ["   "]));

        Assert.Contains("whitespace", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tenant-id")]
    [InlineData("tenant.id")]
    [InlineData("tenant id")]
    [InlineData("tenant:id")]
    public void AKeyContainingAnythingOutsideLettersDigitsOrUnderscoreThrows(string key)
    {
        using var connection = FakeConnection();

        var error = Assert.Throws<ArgumentException>(
            () => new RedisVectorStore(connection, filterableMetadataKeys: [key]));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateKeyThrowsNamingIt()
    {
        using var connection = FakeConnection();

        var error = Assert.Throws<ArgumentException>(
            () => new RedisVectorStore(connection, filterableMetadataKeys: ["tenant", "tenant"]));

        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LettersDigitsAndUnderscoreAreAccepted()
    {
        using var connection = FakeConnection();

        using var store = new RedisVectorStore(
            connection, filterableMetadataKeys: ["tenant_id", "Page2"]);

        Assert.NotNull(store);
    }
}
