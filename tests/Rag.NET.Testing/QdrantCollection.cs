using Xunit;

namespace Rag.NET.Testing;

[CollectionDefinition("Qdrant")]
public sealed class QdrantCollection : ICollectionFixture<QdrantFixture> { }
