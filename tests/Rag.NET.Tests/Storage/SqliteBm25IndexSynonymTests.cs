using Rag.NET.Models;
using Rag.NET.Search;
using Rag.NET.Storage;
using Xunit;

namespace Rag.NET.Tests.Storage;

public class SqliteBm25IndexSynonymTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ragnet-synonym-test-{Guid.NewGuid():N}.db");
    private SqliteBm25Index? _sut;

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null) await _sut.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static TextChunk MakeChunk(string text) => new()
    {
        Text = text, DocumentId = new DocumentId("doc-1"), ChunkIndex = 0,
    };

    [Fact]
    public void Search_IndexKubernetes_QueryK8s_HitsReturned()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        _sut = new SqliteBm25Index(_dbPath, synonymMap: synonyms);
        _sut.Add(0, MakeChunk("deploying kubernetes clusters"));

        var results = _sut.Search("k8s", topK: 5);

        Assert.Single(results);
    }

    [Fact]
    public void Search_NoSynonymMap_ExistingBehaviourUnchanged()
    {
        _sut = new SqliteBm25Index(_dbPath);
        _sut.Add(0, MakeChunk("kubernetes cluster"));

        var results = _sut.Search("k8s", topK: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void Search_SynonymAddedAtRuntime_NewQueryMatches()
    {
        var synonyms = new SynonymMap();
        _sut = new SqliteBm25Index(_dbPath, synonymMap: synonyms);
        _sut.Add(0, MakeChunk("javascript framework"));

        Assert.Empty(_sut.Search("js", topK: 5));

        synonyms.AddGroup("js", "javascript");

        Assert.NotEmpty(_sut.Search("js", topK: 5));
    }
}
