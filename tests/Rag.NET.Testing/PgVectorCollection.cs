using Xunit;

namespace Rag.NET.Testing;

[CollectionDefinition("PgVector")]
public sealed class PgVectorCollection : ICollectionFixture<PgVectorFixture> { }
