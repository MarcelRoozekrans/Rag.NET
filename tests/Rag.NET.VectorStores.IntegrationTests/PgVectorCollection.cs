using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.VectorStores.IntegrationTests;

[CollectionDefinition("PgVector")]
public sealed class PgVectorCollection : ICollectionFixture<PgVectorFixture> { }
