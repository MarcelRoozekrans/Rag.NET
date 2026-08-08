namespace Rag.NET.Hosting.Configuration;

/// <summary>
/// Root of the <c>RagNet</c> configuration section — everything
/// <c>AddRagNetPipelineFromConfiguration</c> needs to build a chat client, an embedding
/// generator, and a vector store.
/// </summary>
public sealed class RagNetOptions
{
    /// <summary>The configuration section name this type binds to: <c>RagNet</c>.</summary>
    public const string SectionName = "RagNet";

    /// <summary>Settings for the OpenAI-compatible chat client.</summary>
    public ChatClientOptions ChatClient { get; set; } = new();

    /// <summary>Settings for the OpenAI-compatible embedding generator.</summary>
    public EmbeddingsOptions Embeddings { get; set; } = new();

    /// <summary>Settings that choose and configure the vector store.</summary>
    public VectorStoreOptions VectorStore { get; set; } = new();
}
