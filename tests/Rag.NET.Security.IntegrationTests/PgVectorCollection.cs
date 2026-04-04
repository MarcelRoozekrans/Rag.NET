using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Security.IntegrationTests;

// xunit v3 resolves [CollectionDefinition] within the current assembly.
[CollectionDefinition("PgVector")]
public sealed class PgVectorCollection : ICollectionFixture<PgVectorFixture> { }
