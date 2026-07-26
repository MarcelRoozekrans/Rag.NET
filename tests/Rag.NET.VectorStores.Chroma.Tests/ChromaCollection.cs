using Xunit;

namespace Rag.NET.Chroma.Tests;

// xunit v3 resolves [CollectionDefinition] within the current assembly. One Chroma
// container is shared by the whole collection (each test isolates itself in its own
// uniquely named Chroma collection).
[CollectionDefinition("Chroma")]
public sealed class ChromaCollection : ICollectionFixture<ChromaContainerFixture>;
