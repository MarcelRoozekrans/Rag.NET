using Xunit;

namespace Rag.NET.Testing;

[CollectionDefinition("Ollama")]
public sealed class OllamaCollection : ICollectionFixture<OllamaFixture> { }
