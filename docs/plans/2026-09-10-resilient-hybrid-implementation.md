# The Decorator That Hid a Capability — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ResilientVectorStore` stops hiding `IHybridSearchable`, so registering `Rag.NET.Resilience` no longer silently disables native hybrid dispatch — and, since 6.2.36, semantic ranking with it.

**Architecture:** One new class, `ResilientHybridVectorStore : ResilientVectorStore, IHybridSearchable`, mirroring `ResilientSparseVectorStore` exactly. It forwards **all three** of the interface's members through the same Polly pipeline as the dense operations — the two defaulted ones are the subtle part, because omitting them still compiles and still passes an `is IHybridSearchable` probe. `ResilientVectorStore.Create` gains a branch that selects it, and a `NotSupportedException` for the sparse-and-hybrid combination it cannot represent. No change to `IHybridSearchable` itself.

**Tech Stack:** .NET 10, C#, xunit v3, NSubstitute, Polly.

**Spec:** `docs/plans/2026-09-10-resilient-hybrid-design.md` — read it first. Its §1 records two measurements that narrowed the scope, and its §3 is the one an implementer is most likely to half-do.

## Global Constraints

- **Issue:** #544. **Phase:** 6.2.37. **Predecessor whose guard depends on this:** #539 / 6.2.36.
- **Conventional commits, header at most 100 characters.** CI lints every commit a PR adds, not just the tip.
- **Do not change `IHybridSearchable`.** The design considered and rejected a `SupportsNativeHybrid` flag (§2). Any diff touching `src/Rag.NET.Abstractions/Abstractions/IHybridSearchable.cs` is a mistake.
- **Do not make `EnsembleBehavior` unwrap the decorator.** Rejected in §2 for two reasons: `IVectorStoreDecorator` exists "so no caller can reach around whatever behaviour the decorator adds", and unwrapping would dispatch outside the resilience pipeline.
- **Do not delete 6.2.36's `native_hybrid_hidden_by_decorator` warning or its two tests.** The resilience case stops reaching it; it stays for any *other* decorator. Design §6, "Out".
- **Do not forward `ICollectionManageable`.** §1 measured that nothing probes it on a resolved `IVectorStore` — it is only ever resolved from DI. Leaving it unforwarded is correct and the class doc will now say why.
- **All three members go through `Pipeline`.** `HybridSearchAsync` is a read against the same backend as `SearchAsync`, idempotent in the same way (§4). The two properties are not pipeline calls — they are plain delegations; "through the pipeline" applies to the method only.
- **`Inner` and `Pipeline` are `private protected`** on `ResilientVectorStore` and the new class lives in the same assembly, so they are reachable. Do not widen them.
- **Test command:** `dotnet test tests/Rag.NET.Resilience.Tests -c Release` and `dotnet test tests/Rag.NET.Tests -c Release`.
- **Baseline must be measured, not assumed.** Task 0.

---

### Task 0: Baseline

**Files:** none.

- [x] **Step 1: Record the baseline for both suites, before touching anything**

```bash
dotnet test tests/Rag.NET.Resilience.Tests -c Release
dotnet test tests/Rag.NET.Tests -c Release
```

Write the pass/skip/fail counts into this file under this step. At the time of writing, `main` was at **105** and **1497** respectively — confirm rather than trust those, they were measured on the previous phase's branch.

**Docker is not required for this phase.** Neither suite uses Testcontainers. If a step seems to need it, something is wrong.

---

### Task 1: The variant, and the three members it must forward

**Files:**

- Create: `src/Rag.NET.Resilience/ResilientHybridVectorStore.cs`
- Test: `tests/Rag.NET.Resilience.Tests/DependencyInjection/ConfigureResilienceTests.cs`

**Interfaces:**

- Consumes: `ResilientVectorStore`'s `private protected` `Inner` and `Pipeline`.
- Produces: `public sealed class ResilientHybridVectorStore : ResilientVectorStore, IHybridSearchable`, with a constructor `(IVectorStore inner, ResiliencePipeline pipeline)` that throws `ArgumentException` when `inner` is not `IHybridSearchable`. Task 2's `Create` branch constructs it.

- [x] **Step 1: Add the test fake**

`ConfigureResilienceTests` already has `private class CountingVectorStore(int failures, Func<Exception> failure) : IVectorStore` — deliberately not sealed — and `private sealed class SparseCountingVectorStore : CountingVectorStore, ISparseSearchable` built on it. Mirror that shape exactly. Add next to `SparseCountingVectorStore`:

```csharp
    /// <summary>
    /// A hybrid-capable inner store. Declares a non-default value for every member of
    /// <see cref="IHybridSearchable"/> so a decorator that silently answers with the interface
    /// defaults is distinguishable from one that forwards — which is the whole risk in #544's fix
    /// (design §3).
    /// </summary>
    private sealed class HybridCountingVectorStore(int failures, Func<Exception> failure)
        : CountingVectorStore(failures, failure), IHybridSearchable
    {
        public int HybridCalls { get; private set; }

        public string? NativeOnlyCapability => "test capability";

        public ScoreScale HybridScoreScale => ScoreScale.Similarity;

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default)
        {
            HybridCalls++;
            return Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new SearchResult
                {
                    Chunk = new TextChunk
                    {
                        Text = textQuery,
                        DocumentId = new DocumentId("hybrid"),
                        ChunkIndex = 0,
                    },
                    Score = 1.0,
                },
            ]);
        }
    }
```

**Both property values are chosen to be non-default and that is the point.** `NativeOnlyCapability`'s interface default is `null` and `HybridScoreScale`'s is `OpaqueRanking`; picking `"test capability"` and `Similarity` means a decorator that fails to forward them returns the *default* rather than the inner store's value, and the assertions below catch it. A fake that returned the defaults would make Steps 2's forwarding tests pass against a decorator that forwards nothing.

`CountingVectorStore`'s constructor takes `(int failures, Func<Exception> failure)`; **read the existing fake before writing this** and match its actual constructor and base-call syntax rather than trusting the snippet.

- [x] **Step 2: Write the failing tests**

Add to `ConfigureResilienceTests`, next to the existing `SparseDecoration_*` and `Decoration_*` tests:

```csharp
    /// <summary>
    /// The defect #544 names: <c>EnsembleBehavior</c> probes the resolved <see cref="IVectorStore"/>,
    /// so a decorator that does not implement <see cref="IHybridSearchable"/> makes native hybrid
    /// dispatch unreachable — silently, and since 6.2.36 taking semantic ranking with it.
    /// </summary>
    [Fact]
    public void HybridDecoration_KeepsTheCapabilityProbeHonest()
    {
        var store = ResilientVectorStore.Create(
            new HybridCountingVectorStore(0, Transient), ResiliencePipeline.Empty);

        Assert.IsType<ResilientHybridVectorStore>(store);
        Assert.IsAssignableFrom<IHybridSearchable>(store);
    }

    /// <summary>
    /// And decoration must not manufacture the capability: a dense-only store stays dense-only.
    /// The mirror of <c>Decoration_DoesNotClaimAChunkLookupTheInnerStoreLacks</c>, and the reason
    /// this is a variant rather than an unconditional implementation (design §2).
    /// </summary>
    [Fact]
    public void Decoration_DoesNotClaimANativeHybridTheInnerStoreLacks()
    {
        var store = ResilientVectorStore.Create(
            new CountingVectorStore(0, Transient), ResiliencePipeline.Empty);

        Assert.IsNotType<ResilientHybridVectorStore>(store);
        Assert.IsNotAssignableFrom<IHybridSearchable>(store);
    }

    /// <summary>
    /// <b>All three members, not just the method.</b> Two of the interface's three members are
    /// defaulted, so a variant that forwards only <c>HybridSearchAsync</c> compiles, passes the
    /// probe above, and dispatches natively — while answering for the backend on the other two.
    /// <c>NativeOnlyCapability</c> is the one that bites: left at its <see langword="null"/>
    /// default, 6.2.36's refusal never fires under resilience, so the capability is restored while
    /// the guard on it stays broken (design §3).
    /// </summary>
    [Fact]
    public async Task HybridDecoration_ForwardsEveryMemberRatherThanAnsweringWithTheDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new HybridCountingVectorStore(0, Transient);
        var decorated = (IHybridSearchable)ResilientVectorStore.Create(inner, ResiliencePipeline.Empty);

        Assert.Equal("test capability", decorated.NativeOnlyCapability, StringComparer.Ordinal);
        Assert.Equal(ScoreScale.Similarity, decorated.HybridScoreScale);

        var results = await decorated.HybridSearchAsync(
            "query text", new float[] { 1f, 0f }, new SearchOptions { TopK = 1 }, ct);

        Assert.Equal(1, inner.HybridCalls);
        Assert.Equal("query text", Assert.Single(results).Chunk.Text, StringComparer.Ordinal);
    }

    /// <summary>
    /// The native hybrid call is retried, symmetric with <c>SearchAsync</c> and with the sparse
    /// variant. <see cref="ResilientVectorStore"/>'s class doc said native hybrid was "not
    /// retried"; that described a consequence of #544 rather than a decision (design §4).
    /// </summary>
    [Fact]
    public async Task HybridSearch_TransientFailure_IsRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var inner = new HybridFailingVectorStore(2, Transient);
        var provider = BuildProviderWithStore(inner);
        var decorated = (IHybridSearchable)provider.GetRequiredService<IVectorStore>();

        var results = await decorated.HybridSearchAsync(
            "query text", new float[] { 1f, 0f }, new SearchOptions { TopK = 1 }, ct);

        Assert.NotEmpty(results);
        Assert.Equal(3, inner.HybridAttempts);
    }
```

**The last test needs a second fake and a real pipeline, not `ResiliencePipeline.Empty`.** `HybridCountingVectorStore` never fails. Add `HybridFailingVectorStore(int failures, Func<Exception> failure)` whose `HybridSearchAsync` increments `HybridAttempts` and throws `failure()` for the first `failures` calls before succeeding — copy the failure-then-succeed shape from `CountingVectorStore`'s own methods, which already do exactly this for `SearchAsync`.

**`BuildProviderWithStore` is a placeholder for whatever this file already uses.** `ConfigureResilienceTests` builds a container and calls `rag.ConfigureResilience()` in its existing retry tests (`VectorStore_TransientFailure_IsRetried` at ~line 318 is the closest model). **Read that test and reuse its exact setup** rather than inventing a helper — the retry policy under test must be the configured one, and `ResiliencePipeline.Empty` would retry nothing and make this test vacuous.

- [x] **Step 3: Run them and confirm they fail for the right reasons**

Run: `dotnet test tests/Rag.NET.Resilience.Tests -c Release --filter "FullyQualifiedName~Hybrid"`

Expected: `HybridDecoration_KeepsTheCapabilityProbeHonest`, `..._ForwardsEveryMember...` and `HybridSearch_TransientFailure_IsRetried` all fail to **compile** (`ResilientHybridVectorStore` does not exist). `Decoration_DoesNotClaimANativeHybridTheInnerStoreLacks` will compile once the type exists and should pass immediately — it is a control.

**If the tests compile at this step, something is wrong** — check you have not accidentally created the class first.

- [x] **Step 4: Write the variant**

Create `src/Rag.NET.Resilience/ResilientHybridVectorStore.cs`:

```csharp
using Polly;
using Rag.NET.Abstractions;
using Rag.NET.Models;
using Rag.NET.Models.Options;

namespace Rag.NET.Resilience;

/// <summary>
/// Hybrid-capable <see cref="ResilientVectorStore"/>: serves <see cref="IHybridSearchable"/> by
/// forwarding the native hybrid query through the same resilience pipeline as the dense ones.
/// </summary>
/// <remarks>
/// <para>
/// Split from the dense-only decorator, the same shape as <see cref="ResilientSparseVectorStore"/>,
/// so the capability probe stays honest: decorating a store without a native hybrid must not make
/// <c>store is IHybridSearchable</c> start returning <see langword="true"/>. Instantiate via
/// <see cref="ResilientVectorStore.Create"/>, which picks the right variant.
/// </para>
/// <para>
/// <b>Every member is forwarded, including the two defaulted ones, and that is not incidental.</b>
/// <see cref="IHybridSearchable.NativeOnlyCapability"/> defaults to <see langword="null"/> and
/// <see cref="IHybridSearchable.HybridScoreScale"/> to <see cref="ScoreScale.OpaqueRanking"/>, so a
/// variant forwarding only <see cref="HybridSearchAsync"/> would compile, pass the probe, dispatch
/// natively — and answer for the backend on the rest. Leaving <c>NativeOnlyCapability</c> at its
/// default is the sharp case: the retrieval pipeline's refusal for a request that cannot reach the
/// native path would never fire under resilience, restoring the capability while breaking the guard
/// on it. That is issue #544 one level in.
/// </para>
/// </remarks>
public sealed class ResilientHybridVectorStore : ResilientVectorStore, IHybridSearchable
{
    private readonly IHybridSearchable _hybrid;

    /// <summary>Creates a hybrid-forwarding decorator over <paramref name="inner"/>.</summary>
    /// <param name="inner">The store to decorate. Must implement <see cref="IHybridSearchable"/>.</param>
    /// <param name="pipeline">The resilience pipeline every call is executed through.</param>
    /// <exception cref="ArgumentException"><paramref name="inner"/> is not <see cref="IHybridSearchable"/>.</exception>
    public ResilientHybridVectorStore(IVectorStore inner, ResiliencePipeline pipeline)
        : base(inner, pipeline) =>
        _hybrid = inner as IHybridSearchable ?? throw new ArgumentException(
            "ResilientHybridVectorStore requires an IHybridSearchable inner store. " +
            "Use ResilientVectorStore.Create, which selects the decorator matching the inner store's capabilities.",
            nameof(inner));

    /// <inheritdoc/>
    /// <remarks>Delegated, never defaulted — see the class remarks.</remarks>
    public string? NativeOnlyCapability => _hybrid.NativeOnlyCapability;

    /// <inheritdoc/>
    /// <remarks>Delegated, never defaulted — see the class remarks.</remarks>
    public ScoreScale HybridScoreScale => _hybrid.HybridScoreScale;

    /// <inheritdoc/>
    /// <remarks>
    /// Retried, symmetric with <see cref="IVectorStore.SearchAsync"/> and with
    /// <see cref="ResilientSparseVectorStore"/>: a native hybrid query is a read against the same
    /// backend, with the same transient failure modes, and idempotent in the same way.
    /// </remarks>
    public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        string textQuery,
        ReadOnlyMemory<float> queryEmbedding,
        SearchOptions options,
        CancellationToken cancellationToken = default) =>
        Pipeline.ExecuteAsync(
            static async (state, ct) =>
                await state.Hybrid.HybridSearchAsync(state.TextQuery, state.QueryEmbedding, state.Options, ct)
                    .ConfigureAwait(false),
            (Hybrid: _hybrid, TextQuery: textQuery, QueryEmbedding: queryEmbedding, Options: options),
            cancellationToken).AsTask();
}
```

**Match `ResilientSparseVectorStore`'s `Pipeline.ExecuteAsync` shape exactly** — the `static` lambda with a state tuple exists to avoid a closure allocation per call, and the sparse variant is the reference. If the tuple's arity causes trouble, read that file rather than falling back to a capturing lambda.

- [x] **Step 5: Run the tests**

Run: `dotnet test tests/Rag.NET.Resilience.Tests -c Release --filter "FullyQualifiedName~Hybrid"`

Expected: PASS, all four. `HybridSearch_TransientFailure_IsRetried` proves the pipeline is wired; the other three prove the probe and the delegations.

- [x] **Step 6: Commit**

```bash
git add src/Rag.NET.Resilience/ResilientHybridVectorStore.cs tests/Rag.NET.Resilience.Tests/DependencyInjection/ConfigureResilienceTests.cs
git commit -m "feat(resilience): forward IHybridSearchable so decoration stops hiding it (#544)"
```

---

### Task 2: `Create` selects it, and refuses what it cannot represent

**Files:**

- Modify: `src/Rag.NET.Resilience/ResilientVectorStore.cs` — `Create` (~line 120) and the class `<remarks>` (~lines 20-32)
- Test: `tests/Rag.NET.Resilience.Tests/DependencyInjection/ConfigureResilienceTests.cs`

**Interfaces:**

- Consumes: Task 1's `ResilientHybridVectorStore`.
- Produces: `Create` returning the hybrid variant for a hybrid inner store, and throwing `NotSupportedException` for a store that is both `ISparseSearchable` and `IHybridSearchable`.

**Note on ordering:** Task 1's tests already call `Create` and expect the hybrid variant, so they fail until this task lands. That is deliberate — the variant and its selection are one behavioural change and splitting them would leave Task 1 green only by testing a constructor nobody calls. **If Task 1 Step 5 passed, this task's `Create` branch was written early; check that it was.**

- [x] **Step 1: Write the failing test for the unrepresentable combination**

```csharp
    /// <summary>
    /// A store that is both sparse and hybrid fits neither variant, and picking one would hide the
    /// other — which is issue #544 again, in whichever direction it was picked. So registration
    /// fails loudly instead of a query quietly doing less than asked (design §5).
    /// </summary>
    /// <remarks>
    /// No shipped store is both today: Azure AI Search and Weaviate are hybrid and not sparse, and
    /// the sparse-capable stores implement no hybrid. This exists so the day one is, it is a build
    /// break rather than a silent capability drop.
    /// </remarks>
    [Fact]
    public void Create_ForAStoreThatIsBothSparseAndHybrid_RefusesRatherThanPickingOne()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ResilientVectorStore.Create(
                new SparseAndHybridCountingVectorStore(0, Transient), ResiliencePipeline.Empty));

        Assert.Contains(nameof(ISparseSearchable), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IHybridSearchable), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SparseAndHybridCountingVectorStore), ex.Message, StringComparison.Ordinal);
    }
```

This needs a third fake — `SparseAndHybridCountingVectorStore : CountingVectorStore, ISparseSearchable, IHybridSearchable` — with trivial member bodies. It exists only to be refused, so its methods may `throw new NotSupportedException()`; nothing calls them.

- [x] **Step 2: Run it**

Run: `dotnet test tests/Rag.NET.Resilience.Tests -c Release --filter "FullyQualifiedName~Create_ForAStoreThatIsBoth"`
Expected: FAIL — no exception is thrown, because `Create` currently matches `ISparseSearchable` first and returns the sparse variant, silently dropping hybrid.

- [x] **Step 3: Rewrite `Create`**

```csharp
    /// <summary>
    /// Creates the decorator variant that preserves <paramref name="inner"/>'s capability
    /// surface: <see cref="ResilientSparseVectorStore"/> when the inner store is
    /// <see cref="ISparseSearchable"/>, <see cref="ResilientHybridVectorStore"/> when it is
    /// <see cref="IHybridSearchable"/>, otherwise a plain <see cref="ResilientVectorStore"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="inner"/> is both <see cref="ISparseSearchable"/> and
    /// <see cref="IHybridSearchable"/>. No variant represents that pair, and choosing either one
    /// would hide the other — the capability-hiding defect this method exists to prevent (#544).
    /// Failing at registration is the alternative to a query that quietly does less than asked.
    /// </exception>
    public static ResilientVectorStore Create(IVectorStore inner, ResiliencePipeline pipeline) =>
        (inner is ISparseSearchable, inner is IHybridSearchable) switch
        {
            (true, true) => throw new NotSupportedException(
                $"{inner.GetType().Name} implements both {nameof(ISparseSearchable)} and " +
                $"{nameof(IHybridSearchable)}, and no resilient decorator variant preserves both. " +
                "Decorating it would silently hide one capability from the retrieval pipeline's " +
                "probe. Add a combined variant to Rag.NET.Resilience, or register the store " +
                "without ConfigureResilience."),
            (true, false) => new ResilientSparseVectorStore(inner, pipeline),
            (false, true) => new ResilientHybridVectorStore(inner, pipeline),
            (false, false) => new ResilientVectorStore(inner, pipeline),
        };
```

**A `switch` on the pair, not a chain of `is` checks.** The chain is what produced the bug being fixed: it answered the first capability it recognised and never asked about the second. The tuple makes the unhandled combination unwritable — a fifth case does not exist, so a future capability forces an edit here rather than silently falling through to a branch that drops it.

- [x] **Step 4: Run the whole resilience suite**

Run: `dotnet test tests/Rag.NET.Resilience.Tests -c Release`
Expected: Task 0's baseline plus the five new tests. **Nothing pre-existing moves** — in particular `SparseDecoration_PreservesTheScoreScaleProbe` and the two `Decoration_*ChunkLookup*` tests, which assert the branches this rewrite touched.

- [x] **Step 5: Rewrite the class `<remarks>`**

The `<para>` beginning "Capability probes: use `Create`" ends with a sentence that is now false:

> `ICollectionManageable` and `IHybridSearchable` are registered separately in DI by the store's own `Use*` extension and therefore resolve to the undecorated store — collection management and native hybrid search are not retried.

Replace the whole `<para>` with:

```csharp
    /// <para>
    /// Capability probes: use <see cref="Create"/> rather than the constructor. It returns a
    /// <see cref="ResilientSparseVectorStore"/> when the inner store is
    /// <see cref="ISparseSearchable"/> and a <see cref="ResilientHybridVectorStore"/> when it is
    /// <see cref="IHybridSearchable"/>, so those probes on the resolved <see cref="IVectorStore"/>
    /// stay honest after decoration; a store that is both is refused, because no variant preserves
    /// the pair. <see cref="IScoreScaleAware"/> and <see cref="IChunkLookup"/> need no variant: the
    /// decorator always implements them and delegates, keeping the claim honest through
    /// <see cref="ScoreScale"/> and <see cref="SupportsChunkLookup"/> respectively.
    /// </para>
    /// <para>
    /// <see cref="ICollectionManageable"/> is deliberately <b>not</b> forwarded, and unlike native
    /// hybrid search that costs nothing: nothing probes it on a resolved <see cref="IVectorStore"/>
    /// — it is registered as its own DI singleton by each store's <c>Use*</c> extension and only
    /// ever resolved directly, where it correctly yields the undecorated store. Collection
    /// management is therefore not retried, by choice. <b>Native hybrid search was in the same
    /// sentence until #544 and did not belong there</b>: it <i>is</i> probed on the resolved store,
    /// by the retrieval pipeline's hybrid dispatch, so not forwarding it did not mean "not retried"
    /// — it meant not reached at all.
    /// </para>
```

- [x] **Step 6: Commit**

```bash
git add src/Rag.NET.Resilience/ResilientVectorStore.cs tests/Rag.NET.Resilience.Tests/DependencyInjection/ConfigureResilienceTests.cs
git commit -m "feat(resilience): select the hybrid variant, and refuse the pair no variant preserves (#544)"
```

---

### Task 3: Prove it end-to-end through the pipeline

**Files:**

- Test: `tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs`

**Interfaces:**

- Consumes: Tasks 1 and 2.
- Produces: nothing new. This task adds only tests.

Tasks 1 and 2 prove the decorator. This proves the thing #544 is actually about: that `EnsembleBehavior` now reaches the native path through a decorator. Without it, the fix is verified only at the layer that was never the complaint.

- [x] **Step 1: Write the test**

`EnsembleBehaviorTests` already has `FakeRankingHybridStore` and `FakeDecoratorOverHybridStore` from 6.2.36. Add a decorator fake that *does* forward, so the pair reads as before-and-after:

```csharp
    /// <summary>
    /// A decorator that forwards <see cref="IHybridSearchable"/>, as
    /// <c>ResilientHybridVectorStore</c> does after #544. The counterpart to
    /// <see cref="FakeDecoratorOverHybridStore"/>, which hides it.
    /// </summary>
    private sealed class FakeForwardingDecoratorOverHybridStore : IVectorStore, IHybridSearchable, IVectorStoreDecorator
    {
        private readonly FakeRankingHybridStore _inner = new();

        public Type InnerStoreType => typeof(FakeRankingHybridStore);

        public string? NativeOnlyCapability => _inner.NativeOnlyCapability;

        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
            string textQuery, ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.HybridSearchAsync(textQuery, queryEmbedding, options, cancellationToken);

        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            ReadOnlyMemory<float> queryEmbedding, SearchOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.SearchAsync(queryEmbedding, options, cancellationToken);

        public Task StoreAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
```

Then two tests:

```csharp
    /// <summary>
    /// #544's fix, at the layer the issue is about: a decorator that forwards
    /// <see cref="IHybridSearchable"/> reaches the native path, where one that hides it does not.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DecoratorForwardsHybridCapability_UsesTheNativePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeForwardingDecoratorOverHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var output = await sut.HandleAsync(
            MakeCtx(new RetrievalOptions { UseHybridSearch = true }), ct,
            (_, _) => throw new InvalidOperationException("must not call next"));

        var only = Assert.Single(output);
        Assert.Equal(new DocumentId("native"), only.Chunk.DocumentId);
    }

    /// <summary>
    /// And the refusal fires through a forwarding decorator too — the case design §3 warns about,
    /// where the capability is restored but the guard on it is not. A decorator that forwarded only
    /// <c>HybridSearchAsync</c> would pass the test above and fail this one.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DecoratorForwardsHybridCapability_StillRefusesWhenNativeIsUnreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new EnsembleBehavior
        {
            Embedder = MakeEmbedder(),
            VectorStore = new FakeForwardingDecoratorOverHybridStore(),
            Bm25Index = Substitute.For<IBm25Index>(),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(
                MakeCtx(new RetrievalOptions { UseHybridSearch = true, MinScore = 0.7 }), ct,
                (_, _) => ValueTask.FromResult<IReadOnlyList<SearchResult>>([])).AsTask());

        Assert.Contains("semantic ranking", ex.Message, StringComparison.Ordinal);
    }
```

**The second test is the one that earns its place.** It is the only test in either suite that fails if `NativeOnlyCapability` is forwarded by the decorator but the refusal is not reached — and it is the pipeline-level statement of design §3.

**`FakeRankingHybridStore` is currently a `private sealed class` with an implicit parameterless constructor** — confirm that before writing `new()` above, and check whether 6.2.36 left it with `NativeOnlyCapability => "semantic ranking"` hard-coded (it did) so the assertion string matches.

- [x] **Step 2: Run and confirm they pass**

Run: `dotnet test tests/Rag.NET.Tests -c Release --filter "FullyQualifiedName~EnsembleBehaviorTests"`
Expected: PASS, all — the pre-existing 6.2.36 tests plus these two. **The `FakeDecoratorOverHybridStore` warning tests must still pass**: that fake still hides the interface, so the warning still fires for it. If they fail, something deleted behaviour Task 0's constraints forbid removing.

- [x] **Step 3: Run the full pipeline suite**

Run: `dotnet test tests/Rag.NET.Tests -c Release`
Expected: Task 0's baseline plus two.

- [x] **Step 4: Commit**

```bash
git add tests/Rag.NET.Tests/Retrieval/Behaviors/EnsembleBehaviorTests.cs
git commit -m "test(retrieval): native dispatch and the refusal both survive a forwarding decorator (#544)"
```

---

### Task 4: Documentation

**Files:**

- Modify: `docs/guide/vector-stores.md` — the "do not combine resilience with semantic ranking" blockquote in the Semantic ranking section
- Modify: `docs/guide/observability.md` — the capability-surfaces paragraph
- Modify: `docs/guide/retrieval.md` — the dispatch-rule prose added by 6.2.36

Three places were changed by 6.2.36 to describe this bug. All three now describe a bug that is fixed, which is worse than describing none.

- [x] **Step 1: `vector-stores.md`**

Remove the blockquote beginning "**Registering `Rag.NET.Resilience` disables native hybrid dispatch entirely**". Replace it with a short note that resilience now preserves native hybrid dispatch and retries it, and that a store which is both sparse and hybrid is refused at registration. Do **not** simply delete it and leave silence: the combination was documented as unsupported, and a reader who followed that advice needs to know it changed.

- [x] **Step 2: `observability.md`**

The capability-surfaces paragraph was corrected by 6.2.36 to say native hybrid is "not merely un-retried but **unreachable**". Correct it a second time: it is now forwarded by `ResilientHybridVectorStore` and retried. Keep the `ICollectionManageable` half — it is still not forwarded, and now the paragraph can say why (nothing probes it on the instance).

- [x] **Step 3: `retrieval.md`**

Remove the sentence added by 6.2.36 stating that registering `Rag.NET.Resilience` disables native hybrid dispatch, and the `native_hybrid_hidden_by_decorator` note attached to it. **Keep the mention of the warning itself** — it still fires for any other decorator — but reword so it no longer names resilience as the example.

- [x] **Step 4: Sweep for anything missed**

```bash
grep -rn "544\|hidden_by_decorator\|resilience" docs/guide/ | grep -in "hybrid"
```

**Read each hit.** Some are correct as they stand.

- [x] **Step 5: Run the docs guards**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`
Expected: the baseline (98 passed / 2 pre-existing skips at the time of writing — confirm against Task 0).

- [x] **Step 6: Commit**

```bash
git add docs/guide/
git commit -m "docs(resilience): native hybrid survives decoration, so stop documenting that it does not (#544)"
```

---

### Task 5: Mutation sweep

**Files:** nothing permanently — apply, test, revert.

- [x] **Step 1: Run each mutation, recording the line mutated AND the named catcher**

**Record the site, not just the description** — 6.2.31's sweep was made unreproducible by naming a mutation without naming where it was applied, and 6.2.34, 6.2.35 and 6.2.36 all kept the corrected habit.

| # | mutation | site | expected catcher |
| --- | --- | --- | --- |
| 1 | drop `NativeOnlyCapability` forwarding (delete the property, take the interface default) | `ResilientHybridVectorStore` | `HybridDecoration_ForwardsEveryMemberRatherThanAnsweringWithTheDefaults`, **and** `HandleAsync_DecoratorForwardsHybridCapability_StillRefusesWhenNativeIsUnreachable`. **This is the row the design was written around** — if only the unit test catches it, say so, because the pipeline-level statement is the one that matters |
| 2 | drop `HybridScoreScale` forwarding | `ResilientHybridVectorStore` | `HybridDecoration_ForwardsEveryMember...` |
| 3 | call `_hybrid.HybridSearchAsync` directly instead of through `Pipeline.ExecuteAsync` | `ResilientHybridVectorStore.HybridSearchAsync` | `HybridSearch_TransientFailure_IsRetried` |
| 4 | `(false, true) => new ResilientVectorStore(inner, pipeline)` | `Create` | `HybridDecoration_KeepsTheCapabilityProbeHonest` |
| 5 | `(true, true) => new ResilientSparseVectorStore(inner, pipeline)` — the old silent behaviour | `Create` | `Create_ForAStoreThatIsBothSparseAndHybrid_RefusesRatherThanPickingOne` |
| 6 | `(false, false) => new ResilientHybridVectorStore(inner, pipeline)` — claim a capability the store lacks | `Create` | `Decoration_DoesNotClaimANativeHybridTheInnerStoreLacks`. Expect an `ArgumentException` from the constructor rather than a clean assertion failure; **that still counts as caught**, and note which it was |
| 7 | delete 6.2.36's `WarnIfADecoratorHidesNativeHybrid` call | `EnsembleBehavior.HandleAsync` | 6.2.36's two warning tests. A control: this phase must not have made that warning dead code |


**Results, measured 2026-09-10. Seven rows, all caught — and row 1 exposed a gap that produced an eighth test.**

| # | mutation | site | outcome |
| --- | --- | --- | --- |
| 1 | drop `NativeOnlyCapability` forwarding | `ResilientHybridVectorStore` | **caught, but only at one layer** — see below |
| 2 | drop `HybridScoreScale` forwarding | `ResilientHybridVectorStore` | **caught** — `HybridDecoration_ForwardsEveryMemberRatherThanAnsweringWithTheDefaults` |
| 3 | call `_hybrid.HybridSearchAsync` directly, bypassing `Pipeline.ExecuteAsync` | `ResilientHybridVectorStore.HybridSearchAsync` | **caught** — `HybridSearch_TransientFailure_IsRetried` |
| 4 | `(false, true) => new ResilientVectorStore(...)` | `Create` | **caught** — all three hybrid tests |
| 5 | `(true, true) => new ResilientSparseVectorStore(...)` — the old silent behaviour | `Create` | **caught** — `Create_ForAStoreThatIsBothSparseAndHybrid_RefusesRatherThanPickingOne` |
| 6 | `(false, false) => new ResilientHybridVectorStore(...)` — claim a capability the store lacks | `Create` | **caught**, loudly: the constructor's `ArgumentException` breaks six tests across both files, not a clean assertion failure. Predicted, and it still counts |
| 7 | delete 6.2.36's `WarnIfADecoratorHidesNativeHybrid` call | `EnsembleBehavior.HandleAsync` | **caught** — both 6.2.36 warning tests. The control: this phase must not have made that warning dead code, and it did not |

**Row 1 is the finding, and the plan predicted it wrong.** The plan expected it to be caught by *both* the resilience unit test and `HandleAsync_DecoratorForwardsHybridCapability_StillRefusesWhenNativeIsUnreachable`. It was caught only by the first. The retrieval test cannot catch it **by construction**: its `FakeForwardingDecoratorOverHybridStore` forwards `NativeOnlyCapability` itself and never touches `ResilientHybridVectorStore`, so mutating the production class cannot affect it.

So the retrieval suite proved the *contract* — a forwarding decorator makes the refusal fire — and the resilience suite proved the *delegation*, and **nothing proved the real decorator satisfies the real contract**. That seam is the exact shape of #544 itself: two layers each correct in isolation that did not compose.

Closed with `tests/Rag.NET.Resilience.Tests/ResilientHybridDispatchTests.cs`, which wires the real `ResilientVectorStore.Create` output into a real `EnsembleBehavior` — the only place both packages are referenced. Row 1 re-run against it: **caught at both layers**, by `ADecoratedHybridStore_StillRefusesWhenTheNativePathIsUnreachable` and the unit test together.

- [x] **Step 2: Re-run any row whose catching test you changed** — rewriting a catching test invalidates its row (6.2.35's lesson).

- [x] **Step 3: Verify the tree is clean** — `git status --short`, only files you meant to change.

---

### Task 6: Issue, roadmap, review and PR

- [ ] **Step 1: Comment on #544** with what shipped and, specifically, whether mutation row 1 was caught by the pipeline-level test or only the unit test. That row is the phase's central claim.

- [ ] **Step 2: `docs/planning/ROADMAP.md`**, the Phase 6.2.37 block — record what the phase found, in its neighbours' style, including the sweep results. **Do not change the `[status: ...]` marker** — `complete-phase` does that after the merge.

- [ ] **Step 3: Run every affected suite.** Enumerate them, do not recall them:

```bash
dotnet test tests/Rag.NET.Resilience.Tests -c Release
dotnet test tests/Rag.NET.Tests -c Release
dotnet test tests/Rag.NET.Memory.Tests -c Release
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
```

`Rag.NET.Memory.Tests` and `Rag.NET.Tests/Memory/` are in the list because `PersistentConversationMemoryScoreScaleTests` calls `ResilientVectorStore.Create` directly — three call sites — and Task 2 rewrote it. **Do not background any of these.**

- [ ] **Step 4: Run the packaging guards**, which `dotnet build` cannot reach:

```powershell
Remove-Item -Recurse -Force artifacts/packages
$v = dotnet dotnet-gitversion /output json /showvariable SemVer
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$v"
```

then `dotnet test tests/Rag.NET.PackageValidation.Tests -c Release`. **Clear the directory first** — the version guard embeds the branch name, and packing over a previous branch's output leaves both generations present, which turns one failure into three. From PowerShell, because Git Bash cannot locate `.git` for GitVersion.

- [ ] **Step 5: Run `pre-push-review`.** Record the verdict and report path here. Fix warnings before the PR.

- [ ] **Step 6: Open the PR.** Fixes #544. Note the behaviour change for existing resilience users (design §4). Record the number here.

---

## Self-review

**Spec coverage** — design §6's five in-scope items: (1) the variant forwarding all three members → Task 1. (2) `Create`'s branch and the `NotSupportedException` → Task 2. (3) the class doc rewritten → Task 2 Step 5. (4) tests, registration-level and `EnsembleBehavior` → Tasks 1, 2, 3. (5) the three doc files → Task 4. Out-of-scope items are honoured by Global Constraints: `IHybridSearchable` untouched, the 6.2.36 warning kept (and guarded by sweep row 7), `ICollectionManageable` unforwarded.

**Placeholder scan** — two spots deliberately name a placeholder rather than inventing an API: `BuildProviderWithStore` in Task 1 Step 2, and the `HybridFailingVectorStore` fake. Both say explicitly to read the existing retry test and mirror it, because guessing a container-building helper that does not exist would produce a test that compiles against nothing. That is an instruction to look, not a TODO.

**Type consistency** — `ResilientHybridVectorStore(IVectorStore, ResiliencePipeline)` is the signature in Task 1 Step 4 and in Task 2 Step 3's `Create`. `NativeOnlyCapability` is `string?` in the fake, the variant and the assertions. `HybridScoreScale` is `ScoreScale` throughout. `DeleteByDocumentIdAsync` takes `string`, matching `IVectorStore` — the same detail 6.2.36's plan got wrong, so it is written correctly here.

**Known risk, stated rather than hidden** — Task 1's tests cannot pass until Task 2 lands, because they call `Create`. This is called out in Task 2's ordering note rather than papered over by having Task 1 construct the variant directly, which would test a constructor no production code calls.
