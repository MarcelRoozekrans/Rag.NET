using Rag.NET.Testing;
using Xunit;

namespace Rag.NET.E2ETests;

// xunit v3 resolves [CollectionDefinition] within the current assembly.
[CollectionDefinition("Ollama")]
public sealed class OllamaCollection : ICollectionFixture<OllamaFixture> { }
