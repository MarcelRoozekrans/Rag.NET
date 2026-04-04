using Xunit;

namespace Rag.NET.AzureAISearch.Tests;

// xunit v3 resolves [CollectionDefinition] within the current assembly.
// This definition groups AzureAISearch tests for serial execution (no shared fixture —
// the simulator container is managed per-class via IAsyncLifetime).
[CollectionDefinition("AzureAISearch")]
public sealed class AzureAISearchCollection { }
