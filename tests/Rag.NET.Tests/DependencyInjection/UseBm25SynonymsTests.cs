using Microsoft.Extensions.DependencyInjection;
using Rag.NET.DependencyInjection;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseBm25SynonymsTests
{
    [Fact]
    public void UseBm25Synonyms_RegistersSynonymMapAsSingleton()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseBm25Synonyms(synonyms));

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<SynonymMap>();

        Assert.Same(synonyms, resolved);
    }

    [Fact]
    public void UseBm25Synonyms_Bm25IndexReceivesSynonymMap()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var services = new ServiceCollection();
        services.AddRagNet(rag => rag.UseBm25Synonyms(synonyms));

        var sp = services.BuildServiceProvider();
        var index = sp.GetRequiredService<InMemoryBm25Index>();

        // Verify synonym expansion works end-to-end via DI
        index.Add(0, new Rag.NET.Models.TextChunk
        {
            Text = "kubernetes deployment",
            DocumentId = new Rag.NET.Models.DocumentId("doc-1"),
            ChunkIndex = 0,
        });

        var results = index.Search("k8s", topK: 5);
        Assert.Single(results);
    }
}
