# Library Comparison — the Defaults Every Entrant Is Measured At

**Date read:** 2026-08-02
**Phase:** 3.14, Task 3 — recorded **before** any comparator entrant was written
**Design:** [`docs/plans/2026-08-02-library-comparison-design.md`](../plans/2026-08-02-library-comparison-design.md) §4

The Phase 3.14 comparison runs each library **at its own defaults**, with one matched element: the
embedder is pinned to `all-MiniLM-L6-v2` for every entrant (design §2). "Default" is not
self-evident, so this page records, from each library's own source at a pinned version, what its
defaults actually are — **before** the entrants exist, so the entrants are written to match this
page rather than this page being written to excuse the entrants.

**Every value cites a file at a version.** A value without a citation is a guess, and none of these
are. Where a library has **no** default — it will not run without the caller choosing — that is
recorded as a finding, together with what the harness will choose and why, rather than a value
being quietly invented.

This is a Markdown reference page rather than a data file because nothing machine-reads it: its
consumers are the Task 4–6 entrants (written by hand to match it) and outside readers who want to
check the readings. The citations and the caveats *are* the content.

---

## Pinned versions

| Package | Version pinned | Source read at | Status |
|---|---|---|---|
| Rag.NET | this repository, commit `241af98` | working tree | current branch |
| `Microsoft.SemanticKernel` | **1.78.0** | tag `dotnet-1.78.0`, [microsoft/semantic-kernel](https://github.com/microsoft/semantic-kernel) | latest stable on nuget.org, 2026-08-02 |
| `Microsoft.SemanticKernel.Connectors.InMemory` | **1.74.0-preview** | tag `dotnet-1.74.0`, matching the pinned package (the cited lines are identical at `dotnet-1.78.0`) | **no stable release exists** — every published version is `-preview` (nuget.org, 2026-08-02) |
| `Microsoft.Extensions.VectorData.Abstractions` | **10.1.0** — the version SK 1.78.0 pins in `dotnet/Directory.Packages.props` | tag `v10.8.0`, [dotnet/extensions](https://github.com/dotnet/extensions) (the library moved there; no `v10.1`-era tag contains it). The one signature this page relies on was verified against the 10.1.0 package's own XML doc: `SearchAsync``1(``0, System.Int32, VectorSearchOptions{`0}, CancellationToken)` | latest stable is 10.8.0 |
| `Microsoft.KernelMemory` / `Microsoft.KernelMemory.Core` | **0.98.250508.3** | tag `packages-0.98.250508.3`, [microsoft/kernel-memory](https://github.com/microsoft/kernel-memory) | **final release. Deprecated on nuget.org** ("legacy … no longer maintained", published 2025-05-09); the repository README opens with "This is an archived research project. The code serves as a learning resource, not production software." (read 2026-08-02) |
| `Microsoft.SemanticKernel.Connectors.Onnx` | 1.78.0-alpha (noted for the wiring section only) | — | alpha only; no stable release exists |

**Kernel Memory's status is itself a finding.** The Stage 1 table will be comparing against a
library whose packages are deprecated and whose repository calls itself an archived research
project. The row is still worth measuring — KM is widely deployed and 0.98.250508.3 is what a user
gets — but the table must say the row is a final, unmaintained version, not a moving competitor.

---

## The matched element: the pinned embedder

Identical for every entrant, and the one deliberate departure from pure defaults (design §2):

- **Model:** `sentence-transformers/all-MiniLM-L6-v2`, ONNX export with token-level output,
  Hugging Face revision `1110a243fdf4706b3f48f1d95db1a4f5529b4d41`, verified against
  SHA-256 `6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452` (`model.onnx`) and
  `07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3` (`vocab.txt`) —
  `.github/workflows/nightly.yml:202-204`.
- **Truncation at 256 tokens** — `all-MiniLM-L6-v2`'s `max_seq_length`; the default of
  `OnnxEmbeddingOptions.MaxTokens` (`src/Rag.NET.Embeddings.Onnx/OnnxEmbeddingOptions.cs:36`).
- **Identity string:** `all-MiniLM-L6-v2/onnx maxTokens=256 mean-pooled-excluding-padding
  l2-normalised` (`tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs:63-64`) —
  mean pooling excluding padding, L2-normalised, exactly what produced the published control
  figures.

Each library's **own** default embedder is recorded below even though it is not used, because
"this library would otherwise have used X" is information a reader needs to interpret the row.

## A protocol parameter, stated up front: retrieval depth is 10 for everyone

The published metric is nDCG@10, and its cutoff is a property of the **measurement**, not of any
library: the harness scores rankings at `BeirHarness.Cutoff = 10`
(`tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs:33`), and the Task 2 control
row already retrieved deep enough to fill 10 documents after chunk-to-document pooling and
self-exclusion (`BeirHarness.cs:479-482`) rather than using Rag.NET's own `TopK = 5` default.

So **every entrant's run file ranks 10 documents per query**, whatever that library's default
top-k is. Each library's default top-k is still recorded below, as information about the library —
but an entrant run at `top-k = 5` would be measured at nDCG@10 with half its ranking missing, and
the table would then be measuring the interaction of a default with a cutoff the library never
knew about. This is an interpretation decision, stated here so it can be disagreed with.

---

## The defaults table

| | **Rag.NET** (`241af98`) | **Semantic Kernel** (1.78.0) | **Kernel Memory** (0.98.250508.3) |
|---|---|---|---|
| Default chunker | `RecursiveChunkingStrategy` | **none — SK has no ingestion pipeline** | `PlainTextChunker` / `MarkDownChunker` via `TextPartitioningHandler` |
| Chunk size | 512 **characters** | no default (`TextChunker` requires the size; utility only, `[Experimental("SKEXP0050")]`) | 1000 **tokens** (cl100k_base) |
| Overlap | 50 characters | `overlapTokens = 0` (when `TextChunker` is opted into) | 100 tokens |
| Default top-k | 5 | **none** at the vector-store API (`top` is a required parameter); 5 in the experimental `TextSearchOptions` | 100 (`MaxMatchesCount`, used when `limit <= 0`; `SearchAsync`'s own default is `limit = -1`) |
| Retrieval mode | dense (cosine); hybrid is per-call opt-in | dense; distance is the store's choice — the InMemory connector defaults to cosine similarity | dense (cosine) against the memory DB |
| Reranks by default | **no** (no `IReranker` registered) | no (no reranker abstraction in the retrieval path) | no (results ordered by similarity only) |
| Default embedder | **none — will not run without one** | **none — will not run without one** (connector registration requires an explicit model id) | **none — builder throws without one**; KM's own "defaults" helper (`WithOpenAIDefaults`) selects OpenAI `text-embedding-ada-002` |
| Default vector store | **none — will not run without one** (`InMemoryVectorStore` exists but is never auto-registered) | **none** at the API level; the natural in-process choice, the InMemory connector, is **preview-only** | `SimpleVectorDb` (volatile, in-memory) — registered by the builder's constructor "for tests and demos" |

Citations and the necessary caveats per library follow.

---

## Rag.NET, at commit `241af98`

- **Chunker:** `RecursiveChunkingStrategy`, auto-registered as the `IChunkingStrategy` —
  `[Singleton(As = typeof(IChunkingStrategy))]`,
  `src/Rag.NET/Chunking/RecursiveChunkingStrategy.cs:10`; separators `"\n\n"`, `"\n"`, `". "`,
  `" "` (line 13). Registration path: the generated `AddRagNETServices()` inside `AddRagNet`
  (`src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs:26-29`).
- **Chunk size / overlap:** `MaxChunkSize = 512`, `Overlap = 50`
  (`src/Rag.NET.Abstractions/Models/Options/ChunkingOptions.cs:8-9`), interpreted as
  **characters** by the built-in strategies
  (`src/Rag.NET/DependencyInjection/RagBuilder.cs:34-36`).
- **Top-k:** `RetrievalOptions.TopK = 5`
  (`src/Rag.NET.Abstractions/Models/Options/RetrievalOptions.cs:11`).
- **Retrieval mode:** dense — `UseHybridSearch = false` (`RetrievalOptions.cs:14`); the BM25 and
  SPLADE arms join only when hybrid is turned on per call. Dense scoring is cosine
  (`src/Rag.NET/Storage/InMemoryVectorStore.cs:9-16` for the in-memory store; the pgvector/Qdrant
  stores score with their engines' cosine operators).
- **Reranking:** off by default. `RetrievalOptions.UseReranking` defaults to `true`
  (`RetrievalOptions.cs:71`) but is inert unless an `IReranker` is registered, which only
  `RagBuilder.UseReranking<T>()` does (`src/Rag.NET/DependencyInjection/RagBuilder.cs:209-213`).
  No reranker is registered by `AddRagNet`.
- **Embedder: no default.** `AddRagNet` registers no
  `IEmbeddingGenerator<string, Embedding<float>>`; retrieval resolves it as a required service
  and the container throws without one. The ONNX generator is an opt-in package
  (`src/Rag.NET.Embeddings.Onnx/RagBuilderExtensions.cs:44`). The comparison provides the pinned
  embedder — the same forced choice every entrant gets.
- **Vector store: no default.** No `IVectorStore` is registered by `AddRagNet`; each store is an
  explicit builder call (`UsePgVector`, `UseQdrant`, …). `InMemoryVectorStore` carries no
  registration attribute and describes itself as "intended for tests, samples, and small corpora"
  (`InMemoryVectorStore.cs:16`).

**Where the Rag.NET rows actually come from.** The comparison's control row runs the **parity
protocol** (one chunk per document) because its job is to reproduce the published figures through
the run-file boundary — its configuration is published in
[retrieval-quality.md](./retrieval-quality.md) and is deliberately *not* Rag.NET's default
chunking. The row that exercises Rag.NET's own chunking defaults (recursive, 512/50, max-pooled
to documents) is the published **real-chunking** leg (retrieval-quality.md, protocol table). Both
protocols retrieve at the cutoff depth, not at `TopK = 5`, per the protocol rule above.

---

## Semantic Kernel 1.78.0 (source at tag `dotnet-1.78.0`)

- **Chunker: none.** Semantic Kernel has no ingestion pipeline; nothing chunks a document unless
  the caller does. The one chunking utility, `TextChunker`
  (`dotnet/src/SemanticKernel.Core/Text/TextChunker.cs`), is `[Experimental("SKEXP0050")]` and has
  **no default size**: `SplitPlainTextLines(string text, int maxTokensPerLine, …)` and
  `SplitPlainTextParagraphs(IEnumerable<string> lines, int maxTokensPerParagraph, int
  overlapTokens = 0, …)` both require the size. Overlap defaults to `0`, and the default token
  counter is the heuristic `length >> 2` (four characters per token) — same file.
  - **No default; the harness will choose:** one vector-store record per document, embedded with
    the pinned generator (which truncates at 256 tokens). Rationale: SK's default is the absence
    of chunking — a user who upserts documents into a vector store without calling the
    experimental splitter gets exactly one record per document — so inventing a `TextChunker`
    size would be the harness's default, not SK's. This also makes the SK row directly comparable
    to the parity control. A reader who thinks `TextChunker` should have been opted in should
    note it cannot run without the harness choosing its size.
- **Top-k: no default at the vector-store API.**
  `IVectorSearchable<TRecord>.SearchAsync<TInput>(TInput searchValue, int top,
  VectorSearchOptions<TRecord>? options = default, …)` requires `top`
  (`src/Libraries/Microsoft.Extensions.VectorData.Abstractions/IVectorSearchable.cs`,
  dotnet/extensions `v10.8.0`; signature verified identical in the 10.1.0 package SK pins).
  `VectorSearchOptions<TRecord>` has no Top property (`VectorSearchOptions.cs`, same tree). The
  higher-level, `[Experimental("SKEXP0001")]` text-search abstraction does default:
  `TextSearchOptions<TRecord>.Top = DefaultTop = 5`
  (`dotnet/src/SemanticKernel.Abstractions/Data/TextSearch/TextSearchOptions.cs:19,42`).
  - **No default; the harness will pass `top` deep enough to rank 10 documents**, because
    nDCG@10 requires it (protocol rule above).
- **Retrieval mode: dense**, with the distance function delegated to the store. In the InMemory
  connector a vector property with no declared distance function is scored with **cosine
  similarity**, descending — `InMemoryCollectionSearchMapping.CompareVectors` maps
  `case null:` to `TensorPrimitives.CosineSimilarity`
  (`dotnet/src/VectorData/InMemory/InMemoryCollectionSearchMapping.cs:30-33` at `dotnet-1.74.0`,
  identical at `dotnet-1.78.0`). Hybrid search is a
  separate opt-in interface (`IKeywordHybridSearchable`, MEVD) that the InMemory connector's
  search path does not enter by default.
- **Reranking: none.** No reranker exists in the vector-search path; nothing to opt out of.
- **Embedder: no default.** Building a `Kernel` registers no embedding generator; every embedding
  connector registration requires an explicit model id — e.g.
  `AddOpenAIEmbeddingGenerator(this IServiceCollection services, string modelId, string apiKey, …)`
  (`dotnet/src/Connectors/Connectors.OpenAI/Extensions/OpenAIServiceCollectionExtensions.DependencyInjection.cs:219-227`).
  So "SK's default embedder" does not exist even as a model name; the row's reader should know SK
  would have refused to run rather than picked one.
- **Vector store: preview-only in process.** The natural in-process store for this comparison,
  `Microsoft.SemanticKernel.Connectors.InMemory`, has **never shipped a stable version**
  (latest `1.74.0-preview`, nuget.org 2026-08-02) — recorded because the table will otherwise
  imply a stable SK configuration existed end-to-end. The entrant will use it anyway (quality is
  unaffected by support status), with the version printed beside the row.

---

## Kernel Memory 0.98.250508.3 (source at tag `packages-0.98.250508.3`)

- **Chunker:** the default ingestion pipeline is `extract → partition → gen_embeddings →
  save_records` (`Constants.DefaultPipeline`, `service/Abstractions/Constants.cs:166-169`). The
  `partition` step is `TextPartitioningHandler`, which splits plain text with `PlainTextChunker`
  and Markdown with `MarkDownChunker`, both constructed over a **`CL100KTokenizer`** — so KM's
  token counts are cl100k_base tokens
  (`service/Core/Handlers/TextPartitioningHandler.cs`, constructor and `InvokeAsync`).
- **Chunk size / overlap:** `TextPartitioningOptions.MaxTokensPerParagraph = 1000`,
  `OverlappingTokens = 100` (`service/Abstractions/Configuration/TextPartitioningOptions.cs`).
  The handler passes these to the chunker explicitly.
  - **A source-vs-source disagreement worth recording:** the chunker's own options default to
    `MaxTokensPerChunk = 1024, Overlap = 0`
    (`extensions/Chunkers/Chunkers/PlainTextChunkerOptions.cs`), but the default pipeline never
    uses those values — `TextPartitioningHandler` always overrides them with
    `TextPartitioningOptions` (1000/100). The **effective** ingestion default is 1000/100;
    1024/0 is what a caller gets only by invoking `PlainTextChunker` directly.
  - **KM's own guard makes 1000 unusable with the pinned embedder.** The handler's constructor
    throws `ConfigurationException` ("chunk too big for embeddings") whenever
    `MaxTokensPerParagraph` exceeds the smallest `ITextEmbeddingGenerator.MaxTokens` in use
    (`TextPartitioningHandler.cs`, constructor). The pinned embedder's honest `MaxTokens` is
    **256**, so KM at its default chunk size **refuses to run**. Recorded as: *no usable default
    with a 256-token embedder; the harness will set `MaxTokensPerParagraph = 256` — the largest
    value KM's own validation accepts for this embedder — and keep the default
    `OverlappingTokens = 100` (valid, since 100 < 256).* This deviation is forced by KM's own
    code, and the Task 5 row must publish it.
- **Top-k:** `IKernelMemory.SearchAsync(…, double minRelevance = 0, int limit = -1, …)`
  (`service/Abstractions/IKernelMemory.cs:202-208`); the search client maps any `limit <= 0` to
  `SearchClientConfig.MaxMatchesCount`, whose default is **100**
  (`service/Core/Search/SearchClient.cs`, `SearchAsync`;
  `service/Abstractions/Search/SearchClientConfig.cs`, `MaxMatchesCount`). So KM's effective
  default search depth is 100 results at `minRelevance = 0`; the run file keeps the top 10 per
  the protocol rule.
- **Retrieval mode: dense.** `SearchAsync` calls `IMemoryDb.GetSimilarListAsync` — vector
  similarity, no keyword arm (`SearchClient.cs`, `SearchAsync`). The interface documents
  `minRelevance` as "Minimum Cosine Similarity required" (`IKernelMemory.cs:197`), and the
  default store scores with cosine similarity
  (`service/Core/MemoryStorage/DevTools/SimpleVectorDb.cs:125`).
- **Reranking: none.** Results are ordered by the store's similarity score; no reranker exists in
  the search path.
- **Embedder: no default — the builder throws.**
  `KernelMemoryBuilder.CheckForMissingDependencies → RequireEmbeddingGenerator` raises
  `ConfigurationException` ("no embedding generators configured for memory ingestion") when none
  was registered (`service/Core/KernelMemoryBuilder.cs`). KM's own batteries-included helper,
  `WithOpenAIDefaults`, selects `DefaultEmbeddingModel = "text-embedding-ada-002"`
  (`extensions/OpenAI/OpenAI/DependencyInjection.cs:24`), which is the closest thing KM has to a
  default embedder and is recorded as such; `OpenAIConfig.EmbeddingModel` itself defaults to
  empty (`extensions/OpenAI/OpenAI/OpenAIConfig.cs:64`).
- **Vector store:** `SimpleVectorDb` (volatile) and `SimpleFileStorage` (volatile) are registered
  by the builder's constructor under the comment "Default configuration for tests and demos"
  (`service/Core/KernelMemoryBuilder.cs`, constructor) — so KM, uniquely among the three, **does**
  run without a store being chosen, on an in-memory cosine store it labels a dev tool.

---

## Wiring the pinned embedder — assessed now so Task 4/5 cannot be surprised

The pinned generator is `OnnxEmbeddingGenerator`, which implements
`Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`
(`src/Rag.NET.Embeddings.Onnx/OnnxEmbeddingGenerator.cs:49`).

**Semantic Kernel — direct fit, no adapter. Not a blocker.**
SK's vector-store stack embeds through Microsoft.Extensions.AI:
`VectorStoreCollectionOptions.EmbeddingGenerator` accepts an `IEmbeddingGenerator`
(`src/Libraries/Microsoft.Extensions.VectorData.Abstractions/VectorStoreCollectionOptions.cs:40`,
dotnet/extensions `v10.8.0`), and `InMemoryCollectionOptions` inherits it unchanged
(`dotnet/src/VectorData/InMemory/InMemoryCollectionOptions.cs:10`, `dotnet-1.74.0`). The entrant
hands the existing `OnnxEmbeddingGenerator` instance to the collection options and passes
`string` search values; identity is then guaranteed by construction and additionally verified by
comparing a known string's vector against `OnnxEmbeddingGenerator` directly (the Task 4
requirement). SK's own local-ONNX path (`BertOnnxTextEmbeddingGenerationService`,
`Microsoft.SemanticKernel.Connectors.Onnx`, alpha-only) exists but is unnecessary — using it
would introduce a second tokenizer/pooling implementation to verify for no gain. Version note:
Rag.NET already builds against `Microsoft.Extensions.AI.Abstractions 10.*`
(`src/Rag.NET/Rag.NET.csproj`), the same major MEVD 10.x expects.

**Kernel Memory — one small adapter. Not a blocker, but it carries the MaxTokens consequence.**
KM's embedding abstraction is its own:
`ITextEmbeddingGenerator : ITextTokenizer` with `int MaxTokens` and
`Task<Embedding> GenerateEmbeddingAsync(string, CancellationToken)`
(`service/Abstractions/AI/ITextEmbeddingGenerator.cs`). The entrant implements it over
`OnnxEmbeddingGenerator` (embedding call is a passthrough; `CountTokens`/`GetTokens` delegate to
the same WordPiece vocabulary the generator already loads) and registers it with
`WithCustomEmbeddingGenerator` (`service/Abstractions/KernelMemoryBuilderExtensions.cs:119-134`).
The adapter must report `MaxTokens = 256` — the truncation the pinned identity string declares —
and that honesty is exactly what triggers KM's chunk-size guard described above. Reporting a
larger `MaxTokens` to keep KM's 1000-token default would silently embed 256-token truncations of
1000-token chunks and misreport the model; the harness lowers the chunk size instead and
publishes the forced change.

**Neither comparator is a Task 4/5 blocker.**

---

## Findings this page exists to record

1. **Kernel Memory is end-of-life at the version measured.** NuGet deprecation ("legacy … no
   longer maintained", final release 0.98.250508.3, 2025-05-09) and a repository README that
   calls the project an archived research project. The row measures the last thing users can
   install.
2. **Semantic Kernel has no defaults where this comparison needs them most**: no chunker (and the
   utility it does ship is experimental and size-less), no top-k at the vector-store API, no
   embedder, and no stable in-process vector store. The SK row is therefore mostly harness
   choices, all recorded above, each forced by an absence in the library.
3. **Kernel Memory's default chunk size cannot be used with the pinned embedder** — its own
   validation throws at 1000 tokens against a 256-token model. The KM row runs at 256/100 by
   KM's own constraint, not by tuning.
4. **KM's chunker disagrees with itself**: `PlainTextChunkerOptions` says 1024/0; the default
   pipeline always overrides to 1000/100. The effective default (1000/100) is the recorded one.
5. **Retrieval depth is a protocol parameter** (10, the metric's cutoff) for every entrant.
   Rag.NET's 5, KM's 100 and SK's "caller must say" are recorded as library facts, not run
   parameters.
6. **No source-vs-documentation disagreement was found** for the values above beyond finding 4;
   where documentation was vaguer than source (KM's docs describe partitioning without the
   embedder guard), source was used and cited.
