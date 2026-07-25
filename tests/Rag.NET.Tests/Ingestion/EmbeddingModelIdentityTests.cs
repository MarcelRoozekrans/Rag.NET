using Microsoft.Extensions.AI;
using NSubstitute;
using Rag.NET.Ingestion;
using Rag.NET.Models.Options;
using Xunit;

namespace Rag.NET.Tests.Ingestion;

public class EmbeddingModelIdentityTests
{
    private static IEmbeddingGenerator<string, Embedding<float>> MakeEmbedder(EmbeddingGeneratorMetadata? metadata)
    {
        var embedder = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        embedder.GetService(typeof(EmbeddingGeneratorMetadata), Arg.Any<object?>()).Returns(metadata);
        return embedder;
    }

    [Fact]
    public void Resolve_ExplicitOverride_WinsOverMetadata()
    {
        var embedder = MakeEmbedder(new EmbeddingGeneratorMetadata("openai", defaultModelId: "text-embedding-3-small"));
        var options = new EmbeddingVersioningOptions { ModelId = "my-custom-id" };

        Assert.Equal("my-custom-id", EmbeddingModelIdentity.Resolve(embedder, options));
    }

    [Fact]
    public void Resolve_MetadataWithProviderAndModel_BuildsCompositeIdentity()
    {
        var embedder = MakeEmbedder(new EmbeddingGeneratorMetadata("openai", defaultModelId: "text-embedding-3-small"));

        Assert.Equal("openai/text-embedding-3-small", EmbeddingModelIdentity.Resolve(embedder, new EmbeddingVersioningOptions()));
    }

    [Fact]
    public void Resolve_MetadataWithModelOnly_ReturnsModelId()
    {
        var embedder = MakeEmbedder(new EmbeddingGeneratorMetadata(defaultModelId: "all-minilm-l6-v2"));

        Assert.Equal("all-minilm-l6-v2", EmbeddingModelIdentity.Resolve(embedder, new EmbeddingVersioningOptions()));
    }

    [Fact]
    public void Resolve_MetadataWithProviderOnly_ReturnsNull()
    {
        // A provider name alone does not identify a model — never guess.
        var embedder = MakeEmbedder(new EmbeddingGeneratorMetadata("fake"));

        Assert.Null(EmbeddingModelIdentity.Resolve(embedder, new EmbeddingVersioningOptions()));
    }

    [Fact]
    public void Resolve_NoMetadataNoOverride_ReturnsNull()
    {
        var embedder = MakeEmbedder(metadata: null);

        Assert.Null(EmbeddingModelIdentity.Resolve(embedder, new EmbeddingVersioningOptions()));
    }

    [Fact]
    public void Resolve_NullEmbedder_OverrideStillWins()
    {
        var options = new EmbeddingVersioningOptions { ModelId = "override-model" };

        Assert.Equal("override-model", EmbeddingModelIdentity.Resolve(embedder: null, options));
    }

    [Fact]
    public void Resolve_NullEmbedderNullOptions_ReturnsNull()
    {
        Assert.Null(EmbeddingModelIdentity.Resolve(embedder: null, options: null));
    }
}
