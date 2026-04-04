# Integration Test Framework Design

**Date:** 2026-04-04
**Feature:** Integration Test Framework

---

## Goal

A sturdy, cost-conscious integration and E2E test framework that exercises the full Rag.NET pipeline — ingestion, chunking, embedding, vector storage, retrieval, answer generation, security, and data providers — against real infrastructure wherever possible, with recorded HTTP for external SaaS connectors and a free Ollama fallback when no LLM API key is available.

---

## Architecture

### Project Structure

```
tests/
  Rag.NET.Testing/                          ← shared library (not a test project)
  Rag.NET.VectorStores.IntegrationTests/    ← PgVector, Qdrant, AzureAISearch
  Rag.NET.Parsers.IntegrationTests/         ← Pdf, Word, Html, Audio, Excel, PowerPoint, Vision
  Rag.NET.Chunking.IntegrationTests/        ← Semantic, CSharp, TokenAware, Templates
  Rag.NET.DataProviders.IntegrationTests/   ← all connectors via WireMock cassettes
  Rag.NET.Security.IntegrationTests/        ← full guard pipeline with real LLM
  Rag.NET.E2ETests/                         ← full chain: ingest → retrieve → answer
```

The existing `Rag.NET.VectorStores.AzureAISearch.Tests` pattern (Docker simulator + `IAsyncLifetime` + `[Collection]`) is the established template; all new test projects follow the same conventions.

---

## `Rag.NET.Testing` Shared Library

A regular class library (no test runner, no xunit runner dependency). All integration test projects reference it.

### Container Fixtures

xUnit `IAsyncLifetime` fixtures wrapping Testcontainers:

| Fixture | Image | Exposes |
|---|---|---|
| `PgVectorFixture` | `ankane/pgvector:latest` | Connection string |
| `QdrantFixture` | `qdrant/qdrant:latest` | HTTP port, gRPC port |
| `OllamaFixture` | `ollama/ollama:latest` | `IChatClient`, model pull on init |

`OllamaFixture` accepts a configurable model list. Defaults:
- Embeddings: `nomic-embed-text`
- Generation: `llama3.2:1b`

### WireMock Helpers

`WireMockServerFixture` wraps `WireMock.Net`:
- **Record mode**: activated by env var `WIREMOCK_RECORD=true`; proxies to real API and saves cassettes under `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/<connector>/`
- **Replay mode** (default): loads cassettes from disk, serves responses without network

### LLM Factory

`TestChatClientFactory` selects the LLM client at test startup:

1. If `OPENROUTER_API_KEY` is set: returns OpenRouter `IChatClient`, model configurable via `OPENROUTER_MODEL` (default: `nvidia/llama-3.1-nemotron-70b-instruct`)
2. Otherwise: returns the `OllamaFixture` client (zero cost, slower)

### xUnit Collection Definitions

Declared in `Rag.NET.Testing` so all projects share them without re-declaring:

```csharp
[CollectionDefinition("PgVector")]
public class PgVectorCollection : ICollectionFixture<PgVectorFixture> { }

[CollectionDefinition("Qdrant")]
public class QdrantCollection : ICollectionFixture<QdrantFixture> { }

[CollectionDefinition("Ollama")]
public class OllamaCollection : ICollectionFixture<OllamaFixture> { }

[CollectionDefinition("WireMock")]
public class WireMockCollection : ICollectionFixture<WireMockServerFixture> { }
```

---

## Per-Package Integration Test Projects

### `Rag.NET.VectorStores.IntegrationTests`

Covers PgVector and Qdrant (same shape as existing AzureAISearch tests). The existing `Rag.NET.VectorStores.AzureAISearch.Tests` project is absorbed here or kept as a sibling — same conventions apply.

Per store:
- `StoreAndSearch_ReturnsRelevantResults`
- `DeleteByDocumentId_RemovesAllChunksForDocument`
- `DeleteByDocumentId_WithMoreChunksThanPageSize_DeletesAllChunksAcrossMultiplePages`
- `Search_WithMetadataFilter_FiltersResults`
- `CollectionManageable_CreateAndDeleteCollection`

### `Rag.NET.Parsers.IntegrationTests`

Real sample files embedded as test resources:

| Parser | Test resource | Assertion |
|---|---|---|
| PDF | `sample.pdf` | Sections extracted, headings non-empty |
| Word | `sample.docx` | Sections extracted |
| HTML | `sample.html` | Text content extracted, tags stripped |
| PowerPoint | `sample.pptx` | One section per slide |
| Excel | `sample.xlsx` | Cell data in section text |
| Audio | `sample.mp3` (~5 s) | Transcription non-empty |
| Image | `sample.png` | Heading = `image_description`, text non-empty |
| Video | `sample.mp4` (~5 s) | At least one `video_scene_0` section |

Vision and Audio tests use `TestChatClientFactory` (OpenRouter or Ollama).

### `Rag.NET.Chunking.IntegrationTests`

Feed real documents (plain text and C# source files) through each chunker:

- **TokenAware**: assert chunk count within expected range, no chunk exceeds token limit
- **Semantic**: uses `TestChatClientFactory`; assert at least 2 chunks from a multi-topic document
- **CSharp**: feed a real `.cs` file; assert each chunk corresponds to a valid declaration
- **Templates**: feed an academic-paper and email sample; assert section headings match template schema

### `Rag.NET.DataProviders.IntegrationTests`

Each connector gets its own test class using `WireMockServerFixture`. Cassettes are committed to the repository under `tests/Rag.NET.DataProviders.IntegrationTests/Cassettes/`.

Per connector:
- `ListDocuments_ReturnsPaginatedResults` — asserts multiple pages are consumed
- `FetchDocument_PopulatesMetadata` — asserts `file_name`, `content_type`, `document_id`

Connectors covered: GitHub, Slack, OneDrive, SharePoint, GoogleDrive, Dropbox, Box, Gmail, MicrosoftTeams, Asana, Confluence, Jira, Notion, Airtable, Bitbucket, GitLab, Zendesk, Web (crawler + sitemap + RSS).

### `Rag.NET.Security.IntegrationTests`

Uses `PgVectorFixture` and `TestChatClientFactory`. Exercises the full ingestion + retrieval pipeline with all security guards registered:

- `InjectionInDocument_IsRedactedBeforeStorage` — ingest document containing injection phrases; assert stored chunk text contains `[REDACTED]`
- `CleanDocument_PassesThroughUnmodified` — assert clean document is unchanged
- `UntrustedChunk_IsDroppedByTrustLevelGuard` — ingest with `trust_level=untrusted`; assert chunk absent from retrieval results
- `PromptHardening_SystemPrefixPresentInLlmCall` — verify `PromptHardeningAnswerEngineDecorator` prepends system message (fake `IChatClient` that captures messages)

### `Rag.NET.E2ETests`

Full pipeline: ingest a small curated document set (3–5 short plain-text docs embedded as resources) → chunk → embed → store in PgVector → retrieve → answer.

Uses `PgVectorFixture` + `TestChatClientFactory`.

One test per answer engine:

```
FullPipeline_Chat_ReturnsRelevantAnswer
FullPipeline_MapReduce_ReturnsRelevantAnswer
FullPipeline_Refine_ReturnsRelevantAnswer
FullPipeline_Dispatching_ReturnsRelevantAnswer
```

Answer relevance assertion: assert answer is non-empty and contains at least one expected keyword from the known document set. Optionally use `Rag.NET.Evaluation` (LLM-as-judge) when `OPENROUTER_API_KEY` is present.

---

## LLM Cost Strategy

| Test area | OpenRouter (key present) | Ollama fallback |
|---|---|---|
| Parsers (vision, audio) | Yes — 1 call per test file | Yes — slower |
| Chunking (semantic) | Yes — 1 call per document | Yes |
| E2E answer generation | Yes — 1 call per engine | Yes |
| Security (LLM classifier) | Yes — 1 call per test | Yes |
| Vector store tests | No LLM needed | No LLM needed |
| Data provider tests | No LLM needed | No LLM needed |

Total OpenRouter calls per full CI run: ~20–30 calls. At Nemotron pricing this is negligible.

---

## Package Dependencies

```xml
<!-- Rag.NET.Testing.csproj -->
<PackageReference Include="Testcontainers" Version="4.*" />
<PackageReference Include="Testcontainers.PostgreSql" Version="4.*" />
<PackageReference Include="WireMock.Net" Version="1.*" />
<PackageReference Include="xunit.v3.extensibility.core" Version="*" />
<PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
```

Each integration test project additionally references `xunit.v3`, `Microsoft.NET.Test.Sdk`, and the relevant `Rag.NET.*` packages under test.

---

## Cassette Management

WireMock cassettes are plain JSON files committed to the repository. To re-record:

```bash
WIREMOCK_RECORD=true dotnet test tests/Rag.NET.DataProviders.IntegrationTests/ --filter "FullyQualifiedName~GitHub"
```

In CI, `WIREMOCK_RECORD` is never set — replay mode is always used.

---

## Error Handling in Tests

- All fixtures implement `IAsyncLifetime`; container startup failures surface as test initialization errors (not flaky test failures)
- `TestContext.Current.CancellationToken` used on all async calls
- OpenRouter failures abort the test with a clear skip message via `Skip.If(!TestChatClientFactory.IsAvailable, "No LLM configured")`
