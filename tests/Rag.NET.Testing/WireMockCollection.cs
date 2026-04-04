using Xunit;

namespace Rag.NET.Testing;

[CollectionDefinition("WireMock")]
public sealed class WireMockCollection : ICollectionFixture<WireMockServerFixture> { }
