using Xunit;

namespace Rag.NET.Pinecone.Tests;

// xunit v3 resolves [CollectionDefinition] within the current assembly. One Pinecone
// Local container is shared by the whole collection (each test isolates itself in its
// own uniquely named indexes and deletes them — the emulator's data-plane port range
// caps concurrent indexes at ten).
[CollectionDefinition("PineconeLocal")]
public sealed class PineconeCollection : ICollectionFixture<PineconeContainerFixture>;
