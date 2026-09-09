using NRedisStack.RedisStackCommands;
using Rag.NET.Models;
using Rag.NET.Models.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Rag.NET.VectorStores.Redis.Tests;

/// <summary>
/// Metadata filtering against real Redis (#513). Before this, <c>SearchAsync</c> never read
/// <c>MetadataFilter</c> at all and a filtered search silently returned unfiltered results.
/// </summary>
public sealed class RedisMetadataFilterTests : IAsyncLifetime
{
    private const int Dimensions = 4;

    private readonly RedisContainer _container =
        new RedisBuilder("redis/redis-stack-server:latest").Build();

    private RedisVectorStore _store = null!;
    private IConnectionMultiplexer _connection = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        _store = new RedisVectorStore(
            _connection, "filter-idx", Dimensions, filterableMetadataKeys: ["tenant", "page"]);
        await _store.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _store.Dispose();
        await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static EmbeddedChunk Chunk(
        string documentId, string text, float[] embedding, params (string Key, MetadataValue Value)[] metadata)
    {
        var dictionary = new Dictionary<string, MetadataValue>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
            dictionary[key] = value;

        return new EmbeddedChunk
        {
            Chunk = new TextChunk
            {
                DocumentId = new DocumentId(documentId),
                ChunkIndex = 0,
                Text = text,
                Metadata = dictionary,
            },
            Embedding = new ReadOnlyMemory<float>(embedding),
        };
    }

    /// <summary>
    /// A declared filterable key is written as its own <c>md_*</c> hash field, alongside the JSON
    /// blob. This is what the index attribute matches; the blob is what the read paths decode.
    /// </summary>
    [Fact]
    public async Task ADeclaredKeyIsWrittenAsItsOwnTagField()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-t", "tagged", [1f, 0f, 0f, 0f], ("tenant", "acme"))], ct);

        var stored = await _connection.GetDatabase().HashGetAsync("filter-idx:doc-t:0", "md_tenant");

        Assert.Equal(
            RedisVectorStore.MetadataToken((MetadataValue)"acme"),
            stored.ToString());
    }

    /// <summary>An undeclared key is written to the blob only — it gets no field of its own.</summary>
    [Fact]
    public async Task AnUndeclaredKeyGetsNoFieldOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [Chunk("doc-o", "other", [1f, 0f, 0f, 0f], ("unlisted", "x"))], ct);

        var stored = await _connection.GetDatabase().HashGetAsync("filter-idx:doc-o:0", "md_unlisted");

        Assert.True(stored.IsNull);
    }

    /// <summary>
    /// <b>This discriminates server-side filtering from client-side.</b> <c>TopK = 1</c> with the
    /// NEAREST chunk excluded by the filter: only a filter applied inside the query can return the
    /// farther chunk. A store that filtered after fetching would return nothing, and one that
    /// ignored the filter — the defect this fixes — would return the near chunk.
    /// </summary>
    [Fact]
    public async Task AFilterIsAppliedInsideTheQuery_NotAfterTheTopKCut()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-near", "near", [1f, 0f, 0f, 0f], ("tenant", "other")),
                Chunk("doc-far", "far", [0f, 1f, 0f, 0f], ("tenant", "acme")),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 1,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                },
            },
            ct);

        var only = Assert.Single(results);
        Assert.Equal("doc-far", only.Chunk.DocumentId.Value);
    }

    /// <summary>A filter of the string "3" must not match metadata written as the number 3.</summary>
    [Fact]
    public async Task AStringFilterDoesNotMatchANumber()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-n", "numeric", [1f, 0f, 0f, 0f], ("page", 3))], ct);

        var asString = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["page"] = "3",
                },
            },
            ct);

        Assert.Empty(asString);

        var asNumber = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["page"] = 3,
                },
            },
            ct);

        Assert.Single(asNumber);
    }

    /// <summary>
    /// A value carrying the characters RediSearch treats as syntax, and the comma a TAG field
    /// splits on. Without escaping the query is malformed or injected; without Base64Url the value
    /// stores as two tags.
    /// </summary>
    [Fact]
    public async Task AValueContainingTagSyntaxAndACommaStillMatchesItself()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-x", "awkward", [1f, 0f, 0f, 0f], ("tenant", "acme, inc - eu:west")),
                Chunk("doc-other", "other", [0f, 1f, 0f, 0f], ("tenant", "other")),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme, inc - eu:west",
                },
            },
            ct);

        var only = Assert.Single(results);
        Assert.Equal("doc-x", only.Chunk.DocumentId.Value);
    }

    /// <summary>
    /// <b>The schema itself must declare the TAG field case-sensitive, not just happen to behave
    /// that way.</b> <see cref="AFilterDoesNotMatchAValueDifferingOnlyInCase"/> cannot tell case
    /// folding apart from a dropped <c>caseSensitive: true</c>: <c>MetadataToken</c> Base64Url-
    /// encodes the value first, and two differently-cased source strings almost never produce
    /// tokens that are themselves case-variants of each other, so RediSearch's default case
    /// folding would not make them collide even without the flag. Reading <c>FT.INFO</c> checks
    /// the schema attribute directly rather than through that indirection.
    /// </summary>
    [Fact]
    public async Task FilterableKeysAreDeclaredAsCaseSensitiveTagFields()
    {
        var info = await _connection.GetDatabase().FT().InfoAsync("filter-idx");

        var found = false;
        foreach (var attribute in info.Attributes)
        {
            var isTenantField = false;
            var isCaseSensitive = false;
            foreach (var value in attribute.Values)
            {
                var text = value.ToString();
                if (string.Equals(text, "md_tenant", StringComparison.Ordinal))
                    isTenantField = true;
                if (string.Equals(text, "CASESENSITIVE", StringComparison.Ordinal))
                    isCaseSensitive = true;
            }

            if (!isTenantField)
                continue;

            found = true;
            Assert.True(isCaseSensitive, "md_tenant should be declared CASESENSITIVE.");
        }

        Assert.True(found, "Expected an md_tenant attribute in the index schema.");
    }

    /// <summary>
    /// <b>Proves the schema flag actually changes matching, not just that it is declared.</b>
    /// <see cref="FilterableKeysAreDeclaredAsCaseSensitiveTagFields"/> only checks that
    /// <c>CASESENSITIVE</c> is present in <c>FT.INFO</c>; it takes on faith that the attribute
    /// does something. The reason it must: <c>MetadataToken</c> Base64Url-encodes the value
    /// before it is stored as a tag, and Base64Url's alphabet uses both letter cases, so two
    /// <em>different</em> values can encode to tokens that are themselves case-variants of one
    /// another — three NUL bytes encode to <c>AAAA</c>, and the UTF-8 bytes of <c>"h\0\0"</c>
    /// encode to <c>aAAA</c>, differing only in the second character's case. Under a
    /// case-folding TAG field, a filter for one would wrongly match a chunk holding the other.
    /// </summary>
    [Fact]
    public async Task ABase64UrlCaseVariantOfAStoredTokenDoesNotMatchIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var threeNulBytes = new string([(char)0, (char)0, (char)0]);
        var hThenTwoNulBytes = new string(['h', (char)0, (char)0]);

        // Sanity-check the premise: the two values really do tokenise to case-variant strings,
        // not to two unrelated strings that merely happen to both fail to match.
        var storedToken = RedisVectorStore.MetadataToken((MetadataValue)threeNulBytes);
        var queriedToken = RedisVectorStore.MetadataToken((MetadataValue)hThenTwoNulBytes);
        Assert.Equal("s:AAAA", storedToken, StringComparer.Ordinal);
        Assert.Equal("s:aAAA", queriedToken, StringComparer.Ordinal);
        Assert.Equal(storedToken, queriedToken, StringComparer.OrdinalIgnoreCase);

        await _store.StoreAsync(
            [Chunk("doc-nul", "nul-tenant", [1f, 0f, 0f, 0f], ("tenant", threeNulBytes))], ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = hThenTwoNulBytes,
                },
            },
            ct);

        Assert.Empty(results);
    }

    /// <summary>Case is significant: TAG fields fold case unless declared not to.</summary>
    [Fact]
    public async Task AFilterDoesNotMatchAValueDifferingOnlyInCase()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-c", "cased", [1f, 0f, 0f, 0f], ("tenant", "ACME"))], ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                },
            },
            ct);

        Assert.Empty(results);
    }

    /// <summary>An empty filter dictionary is not a filter, and must not narrow the page.</summary>
    [Fact]
    public async Task AnEmptyFilterReturnsTheWholePage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-1", "one", [1f, 0f, 0f, 0f], ("tenant", "acme")),
                Chunk("doc-2", "two", [0f, 1f, 0f, 0f], ("tenant", "other")),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal),
            },
            ct);

        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// <b>An undeclared key throws rather than returning an unfiltered page.</b> Returning
    /// everything is what this store did before #513 and is the worst available answer: the caller
    /// asked to narrow and got the opposite, silently. The message names the key and the declared
    /// set so the fix is obvious from the exception alone.
    /// </summary>
    [Fact]
    public async Task AFilterOnAnUndeclaredKeyThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-u", "undeclared", [1f, 0f, 0f, 0f], ("tenant", "acme"))], ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SearchAsync(
                new[] { 1f, 0f, 0f, 0f },
                new SearchOptions
                {
                    TopK = 5,
                    MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["undeclared"] = "x",
                    },
                },
                ct));

        Assert.Contains("undeclared", error.Message, StringComparison.Ordinal);
        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two keys in one filter are AND-ed, not OR-ed: only the chunk matching both comes back, not
    /// every chunk matching either. This is the only test that exercises the join between multiple
    /// clauses in <c>BuildFilterPrefix</c> against a real server.
    /// </summary>
    [Fact]
    public async Task AFilterOnTwoKeysMatchesOnlyChunksSatisfyingBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync(
            [
                Chunk("doc-both", "both", [1f, 0f, 0f, 0f], ("tenant", "acme"), ("page", 3)),
                Chunk("doc-tenant-only", "tenant-only", [0f, 1f, 0f, 0f], ("tenant", "acme"), ("page", 4)),
                Chunk("doc-page-only", "page-only", [0f, 0f, 1f, 0f], ("tenant", "other"), ("page", 3)),
            ],
            ct);

        var results = await _store.SearchAsync(
            new[] { 1f, 0f, 0f, 0f },
            new SearchOptions
            {
                TopK = 5,
                MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                {
                    ["tenant"] = "acme",
                    ["page"] = 3,
                },
            },
            ct);

        var only = Assert.Single(results);
        Assert.Equal("doc-both", only.Chunk.DocumentId.Value);
    }

    /// <summary>
    /// The undeclared-key check must fire regardless of where the bad key falls in iteration
    /// order — a declared key first must not let an undeclared key that follows slip through.
    /// </summary>
    [Fact]
    public async Task AFilterOnADeclaredAndAnUndeclaredKeyThrowsNamingTheUndeclaredOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.StoreAsync([Chunk("doc-u2", "undeclared-second", [1f, 0f, 0f, 0f], ("tenant", "acme"))], ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SearchAsync(
                new[] { 1f, 0f, 0f, 0f },
                new SearchOptions
                {
                    TopK = 5,
                    MetadataFilter = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
                    {
                        ["tenant"] = "acme",
                        ["undeclared"] = "x",
                    },
                },
                ct));

        Assert.Contains("undeclared", error.Message, StringComparison.Ordinal);
        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>An index created before a key was declared filterable must fail loudly at startup.</b>
    /// <c>InitializeAsync</c> leaves an existing index alone — dropping it would discard every
    /// stored vector — so on an upgraded deployment the <c>md_*</c> attributes are simply absent,
    /// and every filtered query would fail at the server or, worse, be answered wrongly. Without
    /// this check the guarantee holds only on a fresh index, which is not where the defect lives.
    /// </summary>
    [Fact]
    public async Task AnIndexMissingADeclaredKeyFailsInitialisation()
    {
        var ct = TestContext.Current.CancellationToken;

        // An index created with NO filterable keys, standing in for one written by an older version.
        using var older = new RedisVectorStore(_connection, "legacy-idx", Dimensions);
        await older.InitializeAsync(ct);

        using var upgraded = new RedisVectorStore(
            _connection, "legacy-idx", Dimensions, filterableMetadataKeys: ["tenant"]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => upgraded.InitializeAsync(ct));

        Assert.Contains("tenant", error.Message, StringComparison.Ordinal);
        Assert.Contains("legacy-idx", error.Message, StringComparison.Ordinal);
    }
}
