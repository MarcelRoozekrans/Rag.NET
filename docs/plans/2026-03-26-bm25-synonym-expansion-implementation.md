# BM25 Synonym Expansion Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `SynonymMap` — a thread-safe, runtime-updatable synonym dictionary — and integrate it into `InMemoryBm25Index` so tokens are expanded at both index time and query time.

**Architecture:** `SynonymMap` stores bidirectional synonym groups in a `Dictionary<string, HashSet<string>>` protected by `ReaderWriterLockSlim`. `InMemoryBm25Index` accepts an optional `SynonymMap` constructor parameter and expands tokens via it in `Tokenize`. `ServiceCollectionExtensions` and `RagBuilder.UseSqlitePersistence` are updated to resolve `SynonymMap?` from DI so call order doesn't matter. `RagBuilder.UseBm25Synonyms` just registers the `SynonymMap` singleton.

**Tech Stack:** C# 13, `System.Collections.Immutable`, `xUnit`, `TestContext.Current.CancellationToken`

---

### Task 1: Implement `SynonymMap`

**Files:**
- Create: `src/Rag.NET/Search/SynonymMap.cs`
- Create: `tests/Rag.NET.Tests/Search/SynonymMapTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/Search/SynonymMapTests.cs`:
```csharp
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class SynonymMapTests
{
    [Fact]
    public void Expand_UnknownTerm_ReturnsEmptySet()
    {
        var map = new SynonymMap();
        Assert.Empty(map.Expand("unknown"));
    }

    [Fact]
    public void AddGroup_BidirectionalExpansion()
    {
        var map = new SynonymMap();
        map.AddGroup("k8s", "kubernetes");

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("k8s", map.Expand("kubernetes"));
    }

    [Fact]
    public void AddGroup_ThreeTermGroup_AllTermsExpandToOthers()
    {
        var map = new SynonymMap();
        map.AddGroup("MI", "myocardial infarction", "heart attack");

        var miExpanded = map.Expand("mi");
        Assert.Contains("myocardial infarction", miExpanded);
        Assert.Contains("heart attack", miExpanded);
        Assert.DoesNotContain("mi", miExpanded); // self excluded

        var miExpanded2 = map.Expand("myocardial infarction");
        Assert.Contains("mi", miExpanded2);
        Assert.Contains("heart attack", miExpanded2);
    }

    [Fact]
    public void AddGroup_NormalisesToLowercase()
    {
        var map = new SynonymMap();
        map.AddGroup("K8S", "Kubernetes");

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("k8s", map.Expand("KUBERNETES"));
    }

    [Fact]
    public void RemoveGroup_RemovesAllListedTerms()
    {
        var map = new SynonymMap();
        map.AddGroup("k8s", "kubernetes");
        map.RemoveGroup("k8s", "kubernetes");

        Assert.Empty(map.Expand("k8s"));
        Assert.Empty(map.Expand("kubernetes"));
    }

    [Fact]
    public void RemoveGroup_UnknownTerms_NoException()
    {
        var map = new SynonymMap();
        map.RemoveGroup("nonexistent"); // should not throw
    }

    [Fact]
    public void AddGroup_FewerThanTwoTerms_ThrowsArgumentException()
    {
        var map = new SynonymMap();
        Assert.Throws<ArgumentException>(() => map.AddGroup("solo"));
    }

    [Fact]
    public void Constructor_WithGroups_ExpandsCorrectly()
    {
        var map = new SynonymMap([
            ["k8s", "kubernetes"],
            ["js", "javascript"],
        ]);

        Assert.Contains("kubernetes", map.Expand("k8s"));
        Assert.Contains("js", map.Expand("javascript"));
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~SynonymMapTests" --no-build
```

Expected: compile error — `SynonymMap` not found.

**Step 3: Write minimal implementation**

`src/Rag.NET/Search/SynonymMap.cs`:
```csharp
namespace Rag.NET.Search;

/// <summary>
/// Thread-safe, runtime-updatable bidirectional synonym dictionary.
/// Terms are normalized to lowercase. Any term in a group expands to all other terms in that group.
/// </summary>
public sealed class SynonymMap : IDisposable
{
    private readonly Dictionary<string, HashSet<string>> _lookup =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ReaderWriterLockSlim _lock = new();

    public SynonymMap() { }

    public SynonymMap(IEnumerable<IReadOnlyCollection<string>> groups)
    {
        foreach (var group in groups)
            AddGroup([.. group]);
    }

    /// <summary>
    /// Adds a synonym group. All terms in the group become bidirectional synonyms.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when fewer than 2 terms are supplied.</exception>
    public void AddGroup(params string[] terms)
    {
        if (terms.Length < 2)
            throw new ArgumentException("A synonym group must contain at least 2 terms.", nameof(terms));

        var normalized = Array.ConvertAll(terms, t => t.ToLowerInvariant());

        _lock.EnterWriteLock();
        try
        {
            foreach (var term in normalized)
            {
                if (!_lookup.TryGetValue(term, out var synonyms))
                {
                    synonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _lookup[term] = synonyms;
                }

                foreach (var other in normalized)
                    if (!string.Equals(term, other, StringComparison.OrdinalIgnoreCase))
                        synonyms.Add(other);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes all listed terms from the lookup. Unknown terms are silently ignored.
    /// </summary>
    public void RemoveGroup(params string[] terms)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var term in terms)
                _lookup.Remove(term.ToLowerInvariant());
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns all synonyms for <paramref name="term"/>. Returns an empty set when the term has no synonyms.
    /// </summary>
    public IReadOnlySet<string> Expand(string term)
    {
        _lock.EnterReadLock();
        try
        {
            return _lookup.TryGetValue(term.ToLowerInvariant(), out var synonyms)
                ? synonyms
                : EmptySet;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose() => _lock.Dispose();

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~SynonymMapTests" --no-build
```

Expected: PASS (8 tests).

**Step 5: Commit**

```bash
git add src/Rag.NET/Search/SynonymMap.cs \
        tests/Rag.NET.Tests/Search/SynonymMapTests.cs
git commit -m "feat: add SynonymMap with bidirectional synonym groups and thread-safe runtime updates"
```

---

### Task 2: Integrate `SynonymMap` into `InMemoryBm25Index` and `SqliteBm25Index`

**Files:**
- Modify: `src/Rag.NET/Search/InMemoryBm25Index.cs`
- Modify: `src/Rag.NET/Storage/SqliteBm25Index.cs`
- Modify: `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `tests/Rag.NET.Tests/Search/InMemoryBm25IndexSynonymTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/Search/InMemoryBm25IndexSynonymTests.cs`:
```csharp
using Rag.NET.Models;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.Search;

public class InMemoryBm25IndexSynonymTests
{
    private static TextChunk Chunk(string text, string docId = "doc-1", int chunkIndex = 0) =>
        new() { Text = text, DocumentId = new DocumentId(docId), ChunkIndex = chunkIndex };

    [Fact]
    public void Search_IndexKubernetes_QueryK8s_HitsReturned()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("deploying kubernetes clusters"));

        var results = index.Search("k8s", topK: 5);

        Assert.Single(results);
    }

    [Fact]
    public void Search_IndexK8s_QueryKubernetes_HitsReturned()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("k8s deployment guide"));

        var results = index.Search("kubernetes", topK: 5);

        Assert.Single(results);
    }

    [Fact]
    public void Search_NoSynonymMap_ExistingBehaviourUnchanged()
    {
        var index = new InMemoryBm25Index();
        index.Add(0, Chunk("kubernetes cluster"));

        // No synonyms — "k8s" should NOT match
        var results = index.Search("k8s", topK: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void Search_ThreeTermSynonymGroup_AllFormsMatch()
    {
        var synonyms = new SynonymMap([["MI", "myocardial infarction", "heart attack"]]);
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("patient had a myocardial infarction"));

        Assert.NotEmpty(index.Search("MI", topK: 5));
        Assert.NotEmpty(index.Search("heart attack", topK: 5));
        Assert.NotEmpty(index.Search("myocardial infarction", topK: 5));
    }

    [Fact]
    public void Search_SynonymAddedAtRuntime_NewQueryMatches()
    {
        var synonyms = new SynonymMap();
        var index = new InMemoryBm25Index(synonyms);
        index.Add(0, Chunk("javascript framework"));

        // Before synonym
        Assert.Empty(index.Search("js", topK: 5));

        // Add synonym at runtime
        synonyms.AddGroup("js", "javascript");

        // After synonym — index was already built, but query expansion now applies
        Assert.NotEmpty(index.Search("js", topK: 5));
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~InMemoryBm25IndexSynonymTests" --no-build
```

Expected: compile error — `InMemoryBm25Index` has no constructor accepting `SynonymMap`.

**Step 3: Update `InMemoryBm25Index`**

In `src/Rag.NET/Search/InMemoryBm25Index.cs`:

Add field and constructor parameter (after the existing constants):
```csharp
private readonly SynonymMap? _synonymMap;

public InMemoryBm25Index(SynonymMap? synonymMap = null)
{
    _synonymMap = synonymMap;
}
```

Update `Add` to pass `_synonymMap`:
```csharp
var tokens = Tokenize(chunk.Text, _synonymMap);
```

Update `Search` to pass `_synonymMap`:
```csharp
var queryTokens = Tokenize(query, _synonymMap);
```

Update `Tokenize` signature and body:
```csharp
internal static List<string> Tokenize(string text, SynonymMap? synonymMap = null)
{
    var tokens = new List<string>();
    var lower = text.ToLowerInvariant();
    var start = -1;
    for (int i = 0; i <= lower.Length; i++)
    {
        bool isAlnum = i < lower.Length && char.IsLetterOrDigit(lower[i]);
        if (isAlnum && start == -1) start = i;
        else if (!isAlnum && start != -1)
        {
            var token = lower[start..i];
            tokens.Add(token);
            if (synonymMap is not null)
                foreach (var syn in synonymMap.Expand(token))
                    tokens.Add(syn);
            start = -1;
        }
    }
    return tokens;
}
```

**Step 4: Update `SqliteBm25Index`**

In `src/Rag.NET/Storage/SqliteBm25Index.cs`:

Change the field and constructor:
```csharp
// Before:
private readonly InMemoryBm25Index _memory = new();

// After:
private readonly InMemoryBm25Index _memory;

public SqliteBm25Index(string dbPath, string? collectionName = null, SynonymMap? synonymMap = null)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
    _dbPath = dbPath;
    _collectionName = collectionName;
    _memory = new InMemoryBm25Index(synonymMap);
}
```

**Step 5: Update `ServiceCollectionExtensions`**

In `src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs`:

Change `InMemoryBm25Index` registration to resolve `SynonymMap?` from DI:
```csharp
// Before:
services.AddSingleton<InMemoryBm25Index>();

// After:
services.AddSingleton<InMemoryBm25Index>(sp => new InMemoryBm25Index(sp.GetService<SynonymMap>()));
```

Also add `using Rag.NET.Search;` if not already present.

**Step 6: Update `RagBuilder.UseSqlitePersistence`**

In `src/Rag.NET/DependencyInjection/RagBuilder.cs`:

Change the `SqliteBm25Index` registration inside `UseSqlitePersistence` to pass `SynonymMap?`:
```csharp
// Before:
Services.AddSingleton<SqliteBm25Index>(_ => new SqliteBm25Index(dbPath, collectionName));

// After:
Services.AddSingleton<SqliteBm25Index>(sp => new SqliteBm25Index(dbPath, collectionName, sp.GetService<SynonymMap>()));
```

**Step 7: Run tests to verify they pass**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~InMemoryBm25IndexSynonymTests" --no-build
```

Expected: PASS (5 tests).

Then run the full unit test suite to confirm nothing is broken:
```
dotnet test tests/Rag.NET.Tests --no-build
```

Expected: all existing tests still pass.

**Step 8: Commit**

```bash
git add src/Rag.NET/Search/InMemoryBm25Index.cs \
        src/Rag.NET/Storage/SqliteBm25Index.cs \
        src/Rag.NET/DependencyInjection/ServiceCollectionExtensions.cs \
        src/Rag.NET/DependencyInjection/RagBuilder.cs \
        tests/Rag.NET.Tests/Search/InMemoryBm25IndexSynonymTests.cs
git commit -m "feat: integrate SynonymMap into InMemoryBm25Index and SqliteBm25Index"
```

---

### Task 3: DI Registration — `RagBuilder.UseBm25Synonyms`

**Files:**
- Modify: `src/Rag.NET/DependencyInjection/RagBuilder.cs`
- Create: `tests/Rag.NET.Tests/DependencyInjection/UseBm25SynonymsTests.cs`

**Step 1: Write the failing tests**

`tests/Rag.NET.Tests/DependencyInjection/UseBm25SynonymsTests.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;
using Rag.NET.Search;
using Xunit;

namespace Rag.NET.Tests.DependencyInjection;

public class UseBm25SynonymsTests
{
    [Fact]
    public void UseBm25Synonyms_RegistersSynonymMapAsSingleton()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagNet(rag => rag.UseBm25Synonyms(synonyms));

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<SynonymMap>();

        Assert.Same(synonyms, resolved);
    }

    [Fact]
    public void UseBm25Synonyms_Bm25IndexReceivesSynonymMap()
    {
        var synonyms = new SynonymMap([["k8s", "kubernetes"]]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRagNet(rag => rag.UseBm25Synonyms(synonyms));

        var sp = services.BuildServiceProvider();
        var index = sp.GetRequiredService<InMemoryBm25Index>();

        // Verify synonym expansion works end-to-end via DI
        index.Add(0, new Rag.NET.Models.TextChunk
        {
            Text = "kubernetes deployment",
            DocumentId = new Rag.NET.Models.DocumentId("doc-1"),
            ChunkIndex = 0,
        });

        var results = index.Search("k8s", topK: 5);
        Assert.Single(results);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/Rag.NET.Tests --filter "FullyQualifiedName~UseBm25SynonymsTests" --no-build
```

Expected: compile error — `UseBm25Synonyms` method not found.

**Step 3: Add `UseBm25Synonyms` to `RagBuilder`**

Add after `UseSqlitePersistence` in `src/Rag.NET/DependencyInjection/RagBuilder.cs`:
```csharp
/// <summary>
/// Registers a <see cref="SynonymMap"/> that expands tokens at both BM25 index time and query time.
/// Synonyms are bidirectional: any term in a group matches all other terms in that group.
/// The map is a singleton — call <see cref="SynonymMap.AddGroup"/> or
/// <see cref="SynonymMap.RemoveGroup"/> at runtime for live updates without restart.
/// </summary>
public RagBuilder UseBm25Synonyms(SynonymMap synonymMap)
{
    Services.AddSingleton(synonymMap);
    return this;
}
```

**Step 4: Run all tests**

```
dotnet test tests/Rag.NET.Tests --no-build
```

Expected: all tests pass.

**Step 5: Commit**

```bash
git add src/Rag.NET/DependencyInjection/RagBuilder.cs \
        tests/Rag.NET.Tests/DependencyInjection/UseBm25SynonymsTests.cs
git commit -m "feat: add RagBuilder.UseBm25Synonyms DI registration"
```

---

## Final Verification

```
dotnet test --no-build
```

All tests must pass. Then use `superpowers:finishing-a-development-branch` to complete the branch.
