using Microsoft.Data.Sqlite;
using Rag.NET.Raptor.Store;
using Xunit;

namespace Rag.NET.Raptor.Store.Tests;

public sealed class SqliteRaptorLeafStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"raptor-leaves-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Leaves_SurviveClosingAndReopeningTheStore()
    {
        var leaves = new[]
        {
            new RaptorLeaf("doc-a", 0, "first", [0.1f, 0.2f, 0.3f]),
            new RaptorLeaf("doc-a", 1, "second", [0.4f, 0.5f, 0.6f]),
            new RaptorLeaf("doc-b", 0, "third", [0.7f, 0.8f, 0.9f]),
        };

        await using (var store = new SqliteRaptorLeafStore(_path))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.AddLeavesAsync(leaves, TestContext.Current.CancellationToken);
        }

        await using var reopened = new SqliteRaptorLeafStore(_path);
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await reopened.CountAsync(TestContext.Current.CancellationToken));

        var all = await reopened.GetAllLeavesAsync(TestContext.Current.CancellationToken);
        var second = all.Single(l => string.Equals(l.DocumentId, "doc-a", StringComparison.Ordinal) && l.ChunkIndex == 1);
        Assert.Equal("second", second.Text);
        Assert.Equal([0.4f, 0.5f, 0.6f], second.Embedding);
    }

    [Fact]
    public async Task AddLeaves_UpsertsOnDocumentAndChunkIndex()
    {
        await using var store = new SqliteRaptorLeafStore(_path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);

        await store.AddLeavesAsync([new RaptorLeaf("doc-a", 0, "original", [1f])], TestContext.Current.CancellationToken);
        await store.AddLeavesAsync([new RaptorLeaf("doc-a", 0, "replaced", [2f])], TestContext.Current.CancellationToken);

        Assert.Equal(1, await store.CountAsync(TestContext.Current.CancellationToken));
        var only = Assert.Single(await store.GetAllLeavesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("replaced", only.Text);
    }

    [Fact]
    public async Task RemoveDocument_RemovesOnlyThatDocumentsLeaves()
    {
        await using var store = new SqliteRaptorLeafStore(_path);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.AddLeavesAsync(
            [new RaptorLeaf("doc-a", 0, "a", [1f]), new RaptorLeaf("doc-b", 0, "b", [2f])],
            TestContext.Current.CancellationToken);

        await store.RemoveDocumentAsync("doc-a", TestContext.Current.CancellationToken);

        var remaining = Assert.Single(await store.GetAllLeavesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("doc-b", remaining.DocumentId);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by default, which keeps a native handle to the
        // file open past DisposeAsync on the store. Cleared here the same way
        // SqliteGraphStoreUnicodeAndIndexTests does, or the delete below throws IOException on
        // Windows because the file is still "in use by another process".
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
