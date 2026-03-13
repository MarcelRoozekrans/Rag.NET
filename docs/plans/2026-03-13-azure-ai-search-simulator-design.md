# Azure AI Search Simulator Integration Tests Design

**Goal:** Replace env-var-gated `AzureAISearchVectorStoreTests` with Testcontainers-backed tests using the [azure-ai-search-simulator](https://github.com/Ellerbach/azure-ai-search-simulator), so Azure AI Search integration tests always run in CI without any Azure subscription.

**Approach:** Inline `ContainerBuilder` in the test class — identical pattern to existing `QdrantVectorStoreTests`. No new abstractions.

---

## Section 1: Architecture

Single file change in `tests/Rag.NET.AzureAISearch.Tests/`. Remove the env-var fields (`_endpoint`, `_apiKey`) and all `Assert.SkipWhen` guards. Add an inline `IContainer` field using `ContainerBuilder` pointing to `ghcr.io/ellerbach/azure-ai-search-simulator:latest`.

The simulator is an ASP.NET app that exposes a compatible Azure AI Search REST API on HTTP port 8080. It accepts any value as an API key. The official `Azure.Search.Documents` SDK connects to it without modification — only the endpoint URI changes from a live Azure URL to `http://localhost:{mappedPort}`.

One new package reference required: `Testcontainers` in `Rag.NET.AzureAISearch.Tests.csproj`.

---

## Section 2: Components

**Modified files:**

- `tests/Rag.NET.AzureAISearch.Tests/Rag.NET.AzureAISearch.Tests.csproj`
  - Add `<PackageReference Include="Testcontainers" Version="4.*" />`

- `tests/Rag.NET.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs`
  - Remove: `_endpoint`, `_apiKey` fields
  - Remove: `if (_endpoint is null || _apiKey is null) return;` in `InitializeAsync`
  - Remove: `if (_endpoint is not null && _apiKey is not null)` guard in `DisposeAsync`
  - Remove: all `Assert.SkipWhen(_sut is null, ...)` in each test
  - Remove: manual `SearchIndexClient.DeleteIndexAsync` cleanup in `DisposeAsync` (container teardown handles it)
  - Add: `IContainer _simulator` field via `ContainerBuilder`
  - Update: `InitializeAsync` to start container and resolve mapped port
  - Update: `DisposeAsync` to dispose container

**No changes** to `src/Rag.NET.AzureAISearch/` — this is test infrastructure only.

---

## Section 3: Data Flow

**Container startup (InitializeAsync):**
1. `await _simulator.StartAsync()` — Testcontainers pulls and starts the container
2. Wait strategy resolves: `UntilHttpRequestIsSucceeded` on port 8080 blocks until the simulator API responds
3. Resolve mapped host port: `_simulator.GetMappedPublicPort(8080)`
4. Construct `AzureAISearchVectorStore` with `new Uri($"http://localhost:{port}")` and `new AzureKeyCredential("test-key")`
5. Call `_sut.InitializeAsync()` to create the index in the simulator

**Per-test execution:**
- Identical to today — `StoreAsync`, `SearchAsync`, `DeleteByDocumentIdAsync` all go through the simulator's REST API
- `Task.Delay(2s)` indexing waits are preserved — the simulator has the same near-real-time indexing behaviour as the real service

**Container teardown (DisposeAsync):**
1. `await _simulator.DisposeAsync()` — container stopped and removed, all data discarded
2. No manual index cleanup needed (replaces the `SearchIndexClient.DeleteIndexAsync` call)

---

## Section 4: Error Handling

**Docker unavailable:** Testcontainers throws `InvalidOperationException` on `StartAsync`. xUnit marks the test as **failed**, not skipped — correct behaviour; CI failure is visible and actionable.

**Simulator startup timeout:** Testcontainers default timeout is 60 seconds. If the simulator doesn't respond within that window, `StartAsync` throws and the test fails with a clear timeout message.

**DisposeAsync safety:** `IContainer.DisposeAsync()` is safe to call even if the container never started (e.g., `StartAsync` threw). No null-guard needed.

---

## Section 5: Testing

No new tests are written. The existing 4 tests become the validation suite — they must all pass against the simulator with no Azure env vars set:

- `StoreAndSearch_ReturnsRelevantResults`
- `DeleteByDocumentId_RemovesAllChunksForDocument`
- `Search_WithMetadataFilter_FiltersResults`
- `CollectionManageable_CreateAndDeleteCollection`

A passing run of all 4 with no environment variables set is the definition of done.
