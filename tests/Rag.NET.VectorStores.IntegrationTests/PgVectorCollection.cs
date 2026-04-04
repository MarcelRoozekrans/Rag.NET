using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.VectorStores.IntegrationTests;

// xunit v3 resolves [CollectionDefinition] within the current assembly.
// This local definition is required even though Rag.NET.Testing defines the same collection,
// because collection fixtures must be discoverable in the same assembly as the test class.
[CollectionDefinition("PgVector")]
public sealed class PgVectorCollection : ICollectionFixture<PgVectorFixture> { }
