using Xunit;

namespace Rag.NET.Weaviate.Tests;

// xunit v3 resolves [CollectionDefinition] within the current assembly. One Weaviate
// container is shared by the whole collection (each test isolates itself in its own class).
[CollectionDefinition("Weaviate")]
public sealed class WeaviateCollection : ICollectionFixture<WeaviateContainerFixture>;
