# Package Reorganization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Reorganize the Rag.NET library into a clean package hierarchy: introduce `Rag.NET.Abstractions`, extract chunking strategies, rename vector store packages, and extract answer engines, query techniques, and memory into separate packages.

**Architecture:** Five sequential phases — each must produce a green build before the next begins. Phase 1 (Abstractions) unblocks everything else. Phases 2-4 are the main extractions. Phase 5 slims downstream package references.

**Tech Stack:** .NET 10, MSBuild, ZeroAlloc.Inject source generators, xunit.v3.

---

## Context: Current Namespace Map

| What | Current namespace | Current project |
|---|---|---|
| Interfaces (IChunkingStrategy etc.) | `Rag.NET.Abstractions` | `Rag.NET` |
| Models (TextChunk, SearchResult etc.) | `Rag.NET.Models` | `Rag.NET` |
| Chunking strategies | `Rag.NET.Chunking` | `Rag.NET` |
| Answer engines | `Rag.NET.AnswerGeneration` | `Rag.NET` |
| HyDE / MultiQuery / SelfQuery | `Rag.NET.HyDE` etc. | `Rag.NET` |
| Memory | `Rag.NET.Memory` | `Rag.NET` |

**Namespaces do NOT change** — only the physical package (csproj) that owns each file changes.

---

## DI Extension Pattern

Each new package owns its own `RagBuilderExtensions.cs` with extension methods on `RagBuilder`. The existing methods in `Rag.NET/DependencyInjection/RagBuilder.cs` that register the moved classes are **deleted** from core and replaced by extension methods in the new packages. Follow the exact pattern of `src/Rag.NET.Reranking.Cohere/RagBuilderExtensions.cs`.

`RagBuilder` is defined in `Rag.NET` core. Extension packages reference `Rag.NET` (not just Abstractions) so they can return `RagBuilder`.

---

## Phase 1: Create `Rag.NET.Abstractions`

### Task 1: Scaffold the project

**Files:**
- Create: `src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj`
- Modify: `Rag.NET.slnx`

**Step 1: Create the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Abstractions</RootNamespace>
    <PackageId>Rag.NET.Abstractions</PackageId>
    <Description>Interfaces and models for the Rag.NET library</Description>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="9.*" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.*" />
    <PackageReference Include="ZeroAlloc.Results" Version="0.*" />
    <PackageReference Include="ZeroAlloc.Specification" Version="0.*" GeneratePathProperty="true" />
    <PackageReference Include="ZeroAlloc.Specification.Generator" Version="0.*" PrivateAssets="all" ExcludeAssets="runtime" GeneratePathProperty="true" />
    <PackageReference Include="ZeroAlloc.ValueObjects" Version="1.*" GeneratePathProperty="true" />
  </ItemGroup>

  <ItemGroup>
    <Analyzer Include="$(PkgZeroAlloc_Specification_Generator)\analyzers\dotnet\cs\ZeroAlloc.Specification.Generator.dll" />
    <Analyzer Include="$(PkgZeroAlloc_ValueObjects)\analyzers\dotnet\cs\ZeroAlloc.ValueObjects.Generator.dll" />
  </ItemGroup>

</Project>
```

**Step 2: Add to solution**

In `Rag.NET.slnx`, add as the **first** project inside the `/src/` folder:
```xml
<Project Path="src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj" />
```

**Step 3: Build to verify scaffold**
```bash
dotnet build src/Rag.NET.Abstractions/Rag.NET.Abstractions.csproj
```
Expected: Build succeeded (empty project).

**Step 4: Commit**
```bash
git add src/Rag.NET.Abstractions/ Rag.NET.slnx
git commit -m "chore: scaffold Rag.NET.Abstractions project"
```

---

### Task 2: Move interfaces and models

**Files:**
- Move: `src/Rag.NET/Abstractions/*.cs` → `src/Rag.NET.Abstractions/Abstractions/`
- Move: `src/Rag.NET/Models/**/*.cs` → `src/Rag.NET.Abstractions/Models/`
- Modify: `src/Rag.NET/Rag.NET.csproj`

**Step 1: Move all 20 interface files**

Copy each file from `src/Rag.NET/Abstractions/` to `src/Rag.NET.Abstractions/Abstractions/`. Do NOT change any file content — namespaces stay as `Rag.NET.Abstractions`. Delete originals from `src/Rag.NET/Abstractions/`.

Files to move: IAnswerEngine.cs, IBm25Index.cs, IChunkRefinementStrategy.cs, IChunkingStrategy.cs, ICollectionManageable.cs, IContentHashStore.cs, IConversationMemory.cs, IDocumentChunkingStrategy.cs, IDocumentParser.cs, IHybridSearchable.cs, IHypotheticalDocumentGenerator.cs, IIngestor.cs, IParentChunkStore.cs, IQueryExpander.cs, IRagDataManager.cs, IRagPipeline.cs, IReranker.cs, IRetriever.cs, ITagIndex.cs, IVectorStore.cs

**Step 2: Move all 38 model files**

Copy each file from `src/Rag.NET/Models/` (including `Options/` subdirectory) to `src/Rag.NET.Abstractions/Models/` (preserving subdirectory structure). Namespaces stay as `Rag.NET.Models` and `Rag.NET.Models.Options`. Delete originals.

**Step 3: Add project reference in `Rag.NET.csproj`**

Add to `src/Rag.NET/Rag.NET.csproj`:
```xml
<ItemGroup>
  <ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
</ItemGroup>
```

Remove from `Rag.NET.csproj` any PackageReferences that are now covered by the Abstractions project (ZeroAlloc.Specification, ZeroAlloc.ValueObjects — keep the rest since core needs them for implementations).

**Step 4: Build the full solution**
```bash
dotnet build Rag.NET.slnx
```
Expected: Build succeeded. If there are missing `using` errors in `Rag.NET` core files, they're already covered because `Rag.NET` references `Rag.NET.Abstractions` which re-exports those namespaces.

**Step 5: Run all tests**
```bash
dotnet test Rag.NET.slnx
```
Expected: All tests pass.

**Step 6: Commit**
```bash
git add src/Rag.NET.Abstractions/ src/Rag.NET/
git commit -m "feat: extract interfaces and models into Rag.NET.Abstractions"
```

---

### Task 3: Update extension packages to reference Abstractions

**Files:**
- Modify: csproj files for all packages listed below

For each of these packages, change their project reference from `Rag.NET` → `Rag.NET.Abstractions` **only if** they don't need pipeline utilities or `RagBuilder`. If they reference `RagBuilder` (for extension methods), keep `Rag.NET` reference.

**Packages to switch to `Rag.NET.Abstractions` reference:**
- `src/Rag.NET.Reranking.Onnx/`
- `src/Rag.NET.Reranking.Cohere/`
- `src/Rag.NET.Parsers.Pdf/`
- `src/Rag.NET.Parsers.Html/`
- `src/Rag.NET.Parsers.Word/`
- `src/Rag.NET.Parsers.Audio/`
- `src/Rag.NET.Parsers.Excel/`
- `src/Rag.NET.Parsers.PowerPoint/`
- `src/Rag.NET.DataProviders/`
- `src/Rag.NET.DataProviders.GitHub/`
- `src/Rag.NET.DataProviders.Web/`
- `src/Rag.NET.DataProviders.AzureBlob/`
- `src/Rag.NET.DataProviders.SharePoint/`
- `src/Rag.NET.DataProviders.OneDrive/`
- `src/Rag.NET.DataProviders.GoogleDrive/`
- `src/Rag.NET.DataProviders.Dropbox/`
- `src/Rag.NET.DataProviders.Box/`
- `src/Rag.NET.DataProviders.Confluence/`
- `src/Rag.NET.DataProviders.Jira/`
- `src/Rag.NET.DataProviders.Notion/`
- `src/Rag.NET.DataProviders.Asana/`
- `src/Rag.NET.DataProviders.Slack/`
- `src/Rag.NET.DataProviders.MicrosoftTeams/`
- `src/Rag.NET.DataProviders.Gmail/`
- `src/Rag.NET.DataProviders.Bitbucket/`
- `src/Rag.NET.DataProviders.Zendesk/`
- `src/Rag.NET.DataProviders.GitLab/`
- `src/Rag.NET.DataProviders.Airtable/`
- `src/Rag.NET.Evaluation/`

**Packages that keep `Rag.NET` reference** (need RagBuilder or pipeline utilities):
- `src/Rag.NET.Raptor/`
- `src/Rag.NET.Graph/`
- `src/Rag.NET.GraphRag/`
- `src/Rag.NET.Api/`
- `src/Rag.NET.Mcp/`
- `src/Rag.NET.Mediator/`

For each package to switch, change:
```xml
<!-- Before -->
<ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />

<!-- After -->
<ProjectReference Include="..\Rag.NET.Abstractions\Rag.NET.Abstractions.csproj" />
```

> **Note:** The relative path `..\..\` may differ. Count directory levels correctly.

**Step: Build and test after each batch**
```bash
dotnet build Rag.NET.slnx && dotnet test Rag.NET.slnx
```
Expected: All green.

**Step: Commit**
```bash
git add src/
git commit -m "refactor: update extension packages to reference Rag.NET.Abstractions directly"
```

---

## Phase 2: Extract Chunking Packages

### Task 4: Extract `Rag.NET.Chunking` (Hierarchical + Code)

**Files:**
- Create: `src/Rag.NET.Chunking/Rag.NET.Chunking.csproj`
- Move: `src/Rag.NET/Chunking/HierarchicalMergerChunkingStrategy.cs` → `src/Rag.NET.Chunking/`
- Move: `src/Rag.NET/Chunking/CodeChunkingStrategy.cs` → `src/Rag.NET.Chunking/`
- Create: `src/Rag.NET.Chunking/RagBuilderExtensions.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Chunking</RootNamespace>
    <PackageId>Rag.NET.Chunking</PackageId>
    <Description>Hierarchical and code-aware chunking strategies for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Move files**

Move `HierarchicalMergerChunkingStrategy.cs` and `CodeChunkingStrategy.cs` to `src/Rag.NET.Chunking/`. Do NOT change file content — namespaces stay as `Rag.NET.Chunking`.

**Step 3: Create `RagBuilderExtensions.cs`**

Read `src/Rag.NET/DependencyInjection/RagBuilder.cs` to find the `UseHierarchicalMerging()` and `UseCodeChunking()` method bodies. Move them here:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Models.Options;

namespace Rag.NET.Chunking;

public static class RagBuilderExtensions
{
    // Copy UseHierarchicalMerging() body from RagBuilder.cs
    public static RagBuilder UseHierarchicalMerging(this RagBuilder builder, HierarchicalMergerOptions? options = null)
    {
        // ... exact body from RagBuilder.cs
    }

    // Copy UseCodeChunking() body from RagBuilder.cs
    public static RagBuilder UseCodeChunking(this RagBuilder builder, CodeChunkingOptions? options = null)
    {
        // ... exact body from RagBuilder.cs
    }
}
```

**Step 4: Remove methods from `RagBuilder.cs`**

Delete `UseHierarchicalMerging()` and `UseCodeChunking()` from `src/Rag.NET/DependencyInjection/RagBuilder.cs`.

**Step 5: Add to solution**

In `Rag.NET.slnx`, add under `/src/` after the Abstractions entry:
```xml
<Project Path="src/Rag.NET.Chunking/Rag.NET.Chunking.csproj" />
```

**Step 6: Build + test**
```bash
dotnet build Rag.NET.slnx && dotnet test Rag.NET.slnx
```
Expected: All green.

**Step 7: Create test project**

Create `tests/Rag.NET.Chunking.Tests/Rag.NET.Chunking.Tests.csproj` (copy structure from `tests/Rag.NET.Tests/`). Add to solution under `/tests/`. Move any existing chunking tests that test Hierarchical or Code strategies from `tests/Rag.NET.Tests/` to this project.

**Step 8: Commit**
```bash
git add src/Rag.NET.Chunking/ src/Rag.NET/ tests/Rag.NET.Chunking.Tests/ Rag.NET.slnx
git commit -m "feat: extract HierarchicalMerger and Code chunking into Rag.NET.Chunking"
```

---

### Task 5: Extract `Rag.NET.Chunking.Semantic`

**Files:**
- Create: `src/Rag.NET.Chunking.Semantic/Rag.NET.Chunking.Semantic.csproj`
- Move: `src/Rag.NET/Chunking/SemanticChunkingStrategy.cs` → `src/Rag.NET.Chunking.Semantic/`
- Create: `src/Rag.NET.Chunking.Semantic/RagBuilderExtensions.cs`
- Modify: `Rag.NET.slnx`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Chunking.Semantic</RootNamespace>
    <PackageId>Rag.NET.Chunking.Semantic</PackageId>
    <Description>Embedding-based semantic chunking strategy for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Move file and create extensions**

Move `SemanticChunkingStrategy.cs`. Keep namespace `Rag.NET.Chunking` (do not change).

Create `RagBuilderExtensions.cs` with `UseSemanticChunking()` and `UseSemanticRefinement()` moved from `RagBuilder.cs`. Delete those methods from `RagBuilder.cs`.

**Step 3: Add to solution, build, test, commit**
```bash
dotnet build Rag.NET.slnx && dotnet test Rag.NET.slnx
git add src/Rag.NET.Chunking.Semantic/ src/Rag.NET/ Rag.NET.slnx
git commit -m "feat: extract SemanticChunkingStrategy into Rag.NET.Chunking.Semantic"
```

---

### Task 6: Extract `Rag.NET.Chunking.TokenAware`

**Files:**
- Create: `src/Rag.NET.Chunking.TokenAware/Rag.NET.Chunking.TokenAware.csproj`
- Move: `src/Rag.NET/Chunking/TokenAwareChunkingStrategy.cs` → `src/Rag.NET.Chunking.TokenAware/`
- Create: `src/Rag.NET.Chunking.TokenAware/RagBuilderExtensions.cs`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Chunking.TokenAware</RootNamespace>
    <PackageId>Rag.NET.Chunking.TokenAware</PackageId>
    <Description>Token-count-aware chunking strategy for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.ML.Tokenizers" Version="0.*" />
    <PackageReference Include="Microsoft.ML.Tokenizers.Data.Cl100kBase" Version="0.*" />
  </ItemGroup>

</Project>
```

**Step 2: Move file, create extension, clean `Rag.NET.csproj`**

Move `TokenAwareChunkingStrategy.cs`. Create `RagBuilderExtensions.cs` with `UseTokenAwareChunking()` moved from `RagBuilder.cs`. Delete from `RagBuilder.cs`.

After moving, remove `Microsoft.ML.Tokenizers` and `Microsoft.ML.Tokenizers.Data.Cl100kBase` from `src/Rag.NET/Rag.NET.csproj` if no other core file uses them.

**Step 3: Add to solution, build, test, commit**
```bash
dotnet build Rag.NET.slnx && dotnet test Rag.NET.slnx
git add src/Rag.NET.Chunking.TokenAware/ src/Rag.NET/ Rag.NET.slnx
git commit -m "feat: extract TokenAwareChunkingStrategy into Rag.NET.Chunking.TokenAware"
```

---

## Phase 3: Rename Vector Store Packages

### Task 7: Rename `Rag.NET.PgVector` → `Rag.NET.VectorStores.PgVector`

**Files:**
- Rename folder: `src/Rag.NET.PgVector/` → `src/Rag.NET.VectorStores.PgVector/`
- Modify: csproj inside (update `PackageId`, `RootNamespace`)
- Rename folder: `tests/Rag.NET.PgVector.Tests/` → `tests/Rag.NET.VectorStores.PgVector.Tests/`
- Modify: `Rag.NET.slnx`

**Step 1: Rename source folder**

```bash
mv src/Rag.NET.PgVector src/Rag.NET.VectorStores.PgVector
```

**Step 2: Update csproj**

In `src/Rag.NET.VectorStores.PgVector/Rag.NET.PgVector.csproj`:
- Rename file to `Rag.NET.VectorStores.PgVector.csproj`
- Update `<PackageId>Rag.NET.VectorStores.PgVector</PackageId>`
- Update `<RootNamespace>Rag.NET.VectorStores.PgVector</RootNamespace>`
- Update `<Description>PostgreSQL pgvector store for Rag.NET</Description>`

Do NOT change namespaces inside .cs files — they can stay as `Rag.NET.PgVector` or be updated to `Rag.NET.VectorStores.PgVector`. Consistency preferred but not required for this phase.

**Step 3: Rename test folder and update test csproj similarly**

```bash
mv tests/Rag.NET.PgVector.Tests tests/Rag.NET.VectorStores.PgVector.Tests
```

Update test csproj filename and project reference path.

**Step 4: Update `Rag.NET.slnx`**

Replace:
```xml
<Project Path="src/Rag.NET.PgVector/Rag.NET.PgVector.csproj" />
<Project Path="tests/Rag.NET.PgVector.Tests/Rag.NET.PgVector.Tests.csproj" />
```
With:
```xml
<Project Path="src/Rag.NET.VectorStores.PgVector/Rag.NET.VectorStores.PgVector.csproj" />
<Project Path="tests/Rag.NET.VectorStores.PgVector.Tests/Rag.NET.VectorStores.PgVector.Tests.csproj" />
```

**Step 5: Build + test**
```bash
dotnet build Rag.NET.slnx && dotnet test tests/Rag.NET.VectorStores.PgVector.Tests
```

**Step 6: Commit**
```bash
git add src/Rag.NET.VectorStores.PgVector/ tests/Rag.NET.VectorStores.PgVector.Tests/ Rag.NET.slnx
git rm -r src/Rag.NET.PgVector/ tests/Rag.NET.PgVector.Tests/ 2>/dev/null || true
git commit -m "refactor: rename Rag.NET.PgVector → Rag.NET.VectorStores.PgVector"
```

---

### Task 8: Rename `Rag.NET.Qdrant` → `Rag.NET.VectorStores.Qdrant`

Same process as Task 7. Rename folder, csproj, test project, update solution.

```bash
git commit -m "refactor: rename Rag.NET.Qdrant → Rag.NET.VectorStores.Qdrant"
```

---

### Task 9: Rename `Rag.NET.AzureAISearch` → `Rag.NET.VectorStores.AzureAISearch`

Same process as Task 7.

```bash
git commit -m "refactor: rename Rag.NET.AzureAISearch → Rag.NET.VectorStores.AzureAISearch"
```

---

## Phase 4: Extract Answer Engines, Query Techniques, Memory

### Task 10: Extract `Rag.NET.AnswerEngines`

**Files:**
- Create: `src/Rag.NET.AnswerEngines/Rag.NET.AnswerEngines.csproj`
- Move: `src/Rag.NET/AnswerGeneration/MapReduceAnswerEngine.cs` → `src/Rag.NET.AnswerEngines/`
- Move: `src/Rag.NET/AnswerGeneration/RefineAnswerEngine.cs` → `src/Rag.NET.AnswerEngines/`
- Move: `src/Rag.NET/AnswerGeneration/DispatchingAnswerEngine.cs` → `src/Rag.NET.AnswerEngines/`
- Keep: `src/Rag.NET/AnswerGeneration/ChatAnswerEngine.cs` stays in core

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.AnswerEngines</RootNamespace>
    <PackageId>Rag.NET.AnswerEngines</PackageId>
    <Description>MapReduce, Refine, and Dispatching answer engines for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Move files, keep namespaces (`Rag.NET.AnswerGeneration`)**

**Step 3: Create `RagBuilderExtensions.cs`**

Add extension methods `UseMapReduceAnswerEngine()`, `UseRefineAnswerEngine()`, `UseDispatchingAnswerEngine()`. Read `RagBuilder.cs` for existing registration patterns and replicate them here. Remove the corresponding methods from `RagBuilder.cs`.

**Step 4: Add test project, add to solution, build, test, commit**
```bash
git commit -m "feat: extract MapReduce/Refine/Dispatching engines into Rag.NET.AnswerEngines"
```

---

### Task 11: Extract `Rag.NET.QueryTechniques`

**Files:**
- Create: `src/Rag.NET.QueryTechniques/Rag.NET.QueryTechniques.csproj`
- Move: `src/Rag.NET/HyDE/LlmHypotheticalDocumentGenerator.cs`
- Move: `src/Rag.NET/MultiQuery/LlmQueryExpander.cs`
- Move: `src/Rag.NET/SelfQuery/SelfQueryBehavior.cs`
- Move: `src/Rag.NET/SelfQuery/SelfQueryOutput.cs`

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.QueryTechniques</RootNamespace>
    <PackageId>Rag.NET.QueryTechniques</PackageId>
    <Description>HyDE, multi-query, and self-query techniques for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Move files, keep existing namespaces**

**Step 3: Create `RagBuilderExtensions.cs`**

Move HyDE, MultiQuery, SelfQuery registration methods from `RagBuilder.cs` / retrieval pipeline builder into extension methods here: `UseHyDE()`, `UseMultiQuery()`, `UseSelfQuery()`. Read `RagBuilder.cs` for the existing method bodies.

**Step 4: Add test project, add to solution, build, test, commit**
```bash
git commit -m "feat: extract HyDE/MultiQuery/SelfQuery into Rag.NET.QueryTechniques"
```

---

### Task 12: Extract `Rag.NET.Memory`

**Files:**
- Create: `src/Rag.NET.Memory/Rag.NET.Memory.csproj`
- Move: `src/Rag.NET/Memory/PersistentConversationMemory.cs` → `src/Rag.NET.Memory/`
- Keep: `src/Rag.NET/Memory/ConversationMemoryPipeline.cs` stays in core (pipeline infrastructure)

**Step 1: Create csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>Rag.NET.Memory</RootNamespace>
    <PackageId>Rag.NET.Memory</PackageId>
    <Description>Persistent SQLite-backed conversation memory for Rag.NET</Description>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rag.NET\Rag.NET.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.5" />
  </ItemGroup>

</Project>
```

**Step 2: Move file, create `RagBuilderExtensions.cs`**

Move `UsePersistentMemory()` registration from `RagBuilder.cs` into extension method here. Delete from `RagBuilder.cs`.

If `Microsoft.Data.Sqlite` is now only needed by `Rag.NET.Memory` (not core), remove it from `Rag.NET.csproj`. Check whether `SqliteBm25Index`, `SqliteDocumentStore`, `SqliteParentChunkStore`, `SqliteContentHashStore` in core also use it — if yes, keep it in core too.

**Step 3: Add test project, add to solution, build, test, commit**
```bash
git commit -m "feat: extract PersistentConversationMemory into Rag.NET.Memory"
```

---

## Phase 5: Final verification

### Task 13: Full solution build and test

**Step 1: Build entire solution**
```bash
dotnet build Rag.NET.slnx
```
Expected: 0 errors, 0 warnings (TreatWarningsAsErrors=true).

**Step 2: Run all tests**
```bash
dotnet test Rag.NET.slnx
```
Expected: All tests pass.

**Step 3: Verify package structure matches design**

Check that these packages exist in `src/`:
- `Rag.NET.Abstractions`
- `Rag.NET.Chunking`
- `Rag.NET.Chunking.Semantic`
- `Rag.NET.Chunking.TokenAware`
- `Rag.NET.VectorStores.PgVector`
- `Rag.NET.VectorStores.Qdrant`
- `Rag.NET.VectorStores.AzureAISearch`
- `Rag.NET.AnswerEngines`
- `Rag.NET.QueryTechniques`
- `Rag.NET.Memory`

**Step 4: Update `docs/reference/features.md`**

Update the package names in the features doc to reflect new names (vector store packages).

**Step 5: Final commit**
```bash
git add docs/
git commit -m "docs: update package names in features reference for reorganization"
```

---

## Gotchas & Notes

- **ZeroAlloc.Inject `[Singleton]` attributes**: If any moved class has `[Singleton(As = typeof(...))]` attributes, the source generator in the original project won't pick them up anymore. Move the generator reference to the new package's csproj or convert to explicit `AddSingleton` calls in the extension method.
- **`InternalsVisibleTo`**: `Rag.NET.csproj` grants visibility to `Rag.NET.Tests`. If internal members are accessed in moved test files, add matching `InternalsVisibleTo` to the new package csproj.
- **Relative paths in csproj**: When moving files between projects, double-check `..\..\` path depth for project references.
- **`RagBuilder` DI namespace**: Extension methods must `using Rag.NET.DependencyInjection;` to access `RagBuilder`.
- **Green build required**: Never commit a broken build. If Phase 2 breaks something, fix before committing.
