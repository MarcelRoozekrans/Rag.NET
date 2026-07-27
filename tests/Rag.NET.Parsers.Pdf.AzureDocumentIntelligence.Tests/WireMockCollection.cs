using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.Parsers.Pdf.AzureDocumentIntelligence.Tests;

// xunit v3 resolves [CollectionDefinition] within the current assembly.
// This local definition is required even though Rag.NET.Testing defines the same fixture,
// because collection fixtures must be discoverable in the same assembly as the test class.
[CollectionDefinition("WireMock")]
public sealed class WireMockCollection : ICollectionFixture<WireMockServerFixture> { }
