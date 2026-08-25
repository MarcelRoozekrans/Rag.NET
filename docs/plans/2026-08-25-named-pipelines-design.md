# Named pipelines — design

**Issue:** [#342](https://github.com/MarcelRoozekrans/Rag.NET/issues/342). **Phase:** 6.2.7.
**Status:** design approved 2026-08-25.

## The problem

Everything `AddRagNet` registers is an **unkeyed singleton**, so one container is one pipeline is one
index. `UseAzureAISearch` constructs a store bound to one index and registers that instance as
`IVectorStore` / `IHybridSearchable` / `ICollectionManageable`; `ICollectionManageable` can create and
delete *other* indexes, but reads and writes always go to the bound one.

A caller who wants per-tenant isolation therefore has no way to express it in configuration.

### Why a metadata filter is not the answer

Tagging documents at ingest and passing `RetrievalOptions.MetadataFilter` per query works, and on
Azure the filter is pushed down server-side into the OData `$filter` — a real pre-filter. It is still
not isolation:

1. **A filter is caller-supplied, per query.** Forget it once and you have cross-read. An index is a
   static binding; you cannot forget it.
2. **`UseRbac` filters *after* retrieval.** `RbacRetrievalGuard.Inspect` drops the other tenant's
   chunks in-process — after the store returned them, and at the cost of the caller's `TopK`.
3. **The BM25 arm ignored the filter entirely** until #350. That is the sharpest evidence: the
   conditions under which a per-query filter silently stops being a boundary are not something a
   caller can be expected to know, because until recently they included "your store does not have
   native hybrid search, or you set `MinScore`, or you supplied `EnsembleOptions`".

Isolation belongs in the registration, not in every query.

## Approaches considered

### Rejected — routing inside the vector store

The request's original form. `StoreAsync` and `SearchAsync` could route on a metadata key, but
**`DeleteByDocumentIdAsync(documentId, ct)` carries no key**, so a router would have to fan out
across every index or guess. `IChunkLookup`, `IHybridSearchable`, `ISparseSearchable` and
`ICollectionManageable` would each need routing too, and a routing decorator would stack with
`ResilientVectorStore` — which the `IVectorStoreDecorator` contract explicitly says never happens
today.

### Rejected — keyed registrations for every service

Keying every service means a keyed variant of **every `Use*` method** — measured at **69 distinct
method names across 74 declarations** — plus threading the key through the pipeline builders
(`RetrievalPipelineBuilder.cs:130` resolves behaviours *by type* from the container) and through
`CompositionClaimRegistry`.

*(The issue's own analysis cited 43. The real count is larger, which makes this option more
expensive than it was rejected for, not less.)*

### Chosen — a child provider per name

`AddRagNet("docs", …)` composes into its own inner `IServiceCollection` and builds a child provider,
exposed through `IRagPipelineFactory.Get("docs")`.

**Every existing `Use*` method works unchanged**, because they all operate on an `IServiceCollection`.
Nothing needs a keyed overload, nothing threads a key through the builders, and
`CompositionClaimRegistry` keeps working per-collection exactly as it does now. That is the whole
argument for this shape over the previous one.

### Why that holds: `RagBuilder` is not tied to `AddRagNet`

```csharp
public sealed class RagBuilder(IServiceCollection services) : IRagBuilder
{
    public IServiceCollection Services { get; } = services;
```

`RagBuilder` is a thin wrapper over an `IServiceCollection`, and `AddRagNet` merely does
`new RagBuilder(services); configure?.Invoke(builder)`. The `Use*` methods are extension methods on
`TBuilder : IRagBuilder` that only ever touch `builder.Services`.

**So pointing the existing builder at a different collection is the entire mechanism.** There is no
parallel composition system to build, and no `Use*` method that needs to know it is being called
inside a named block. Every claim in this document about "the same methods keep working" rests on
this one property.

## Design

### 1. Composition

```csharp
services.AddRagNetShared(rag => rag.UseOnnxEmbeddings(o => { … }));   // one ONNX session
services.AddRagNet("docs",    rag => rag.UseAzureAISearch(endpoint, "docs-index",    credential));
services.AddRagNet("support", rag => rag.UseAzureAISearch(endpoint, "support-index", credential));

var pipeline = provider.GetRequiredService<IRagPipelineFactory>().Get("docs");
```

Resolution order is **child first, then the shared parent**. So the embedder, `IChatClient` and
`HttpClient`s are singular; stores, indexes, caches and the pipelines themselves are per-name.

**How the fallback actually works — forwarding descriptors, not a composite provider.** A composite
`IServiceProvider` that tries child then parent does not work: services *inside* the child resolve
their constructor dependencies from the child's own provider, not from the wrapper, so a child
service needing `IEmbeddingGenerator` would still fail.

Instead, `AddRagNetShared` records which `ServiceType`s it registered — by snapshotting the
collection around the callback — and each named block adds a forwarding singleton for exactly those
types:

```csharp
foreach (var serviceType in sharedServiceTypes)
{
    var instance = rootProvider.GetRequiredService(serviceType);
    childServices.Replace(ServiceDescriptor.Singleton(serviceType, instance));
}
```

The set is known exactly, so there is no unknown-type problem, and sharing stays visible as a
descriptor rather than as ambient provider behaviour.

**The descriptor must be an *instance*, not a factory lambda — this is not a style choice.** An
earlier draft of this section wrote
`childServices.AddSingleton(serviceType, _ => rootProvider.GetRequiredService(serviceType))`, and it
is wrong in a way that only shows at shutdown: `ServiceProvider` captures anything realised through a
**factory call site** for disposal *in whichever container ran the factory*. Each child would
therefore take ownership of the shared instance, so disposing one named pipeline would dispose the
embedding model out from under every other one — and disposing a second would double-dispose it.
Reproduced against `Microsoft.Extensions.DependencyInjection` 10.0.11 during implementation.

A **constant** call site is excluded from that capture, so an instance descriptor leaves ownership
with the root, which is what §4 requires. `Replace` rather than `Add` because the named block's own
`configure` may already have registered the same service type.

**This is also why `AddRagNetShared` is a distinct call rather than inferred from the outer
collection.** The outer collection contains the host's own registrations — logging, configuration,
`HttpClient`s, everything unrelated. Forwarding all of it would make every child depend on the
host's container shape. Forwarding what one block deliberately declared is a bounded, reviewable set.

### 1a. The unnamed form is untouched, and that is a hard constraint

```csharp
services.AddRagNet(rag => rag.UseAzureAISearch(…).UseOnnxEmbeddings(…));
var pipeline = provider.GetRequiredService<IRagPipeline>();
```

**This keeps working exactly as it does today, registering into the root container.** Nothing about
the bootstrap a reader learns from `getting-started.md` changes, and a caller who never wants a
second pipeline never meets `IRagPipelineFactory` or `AddRagNetShared` at all. Named pipelines are
**purely additive**.

That is a constraint rather than a courtesy. The guide documents resolving pipeline internals
straight from the root provider — `docs/guide/vector-stores.md:211`, `:346` and `:544` all do
`provider.GetRequiredService<IVectorStore>()`, and `getting-started.md:83` resolves `IRagPipeline`.
Routing the unnamed form through a child provider would break every one of those documented lines.
So it is not routed through one.

**The two forms compose.** A caller can keep their default pipeline and add named ones beside it:

```csharp
services.AddRagNetShared(rag => rag.UseOnnxEmbeddings(…));            // one session, shared
services.AddRagNet(rag => rag.UseAzureAISearch(…, "default-index"));  // the root pipeline, as today
services.AddRagNet("docs", rag => rag.UseAzureAISearch(…, "docs-index"));
```

**Corrected 2026-08-25.** An earlier draft of this paragraph claimed `IRagPipelineFactory` "also
exposes the root pipeline". **It does not, and no version of this design ever specified it** — the
interface is `Get(string)` and `Contains(string)`. The claim was never implemented because it was
never planned; it was written here and nowhere else.

The root pipeline is resolved the way it always was, with
`GetRequiredService<IRagPipeline>()`. Mixing that with `Get(name)` in one application is the intended
usage, not a shortcoming: the direct resolve is what every existing reader already knows, and named
pipelines sit beside it rather than replacing it.

### 1b. Verified before relying on it

The design assumes parallel containers are safe. Checked rather than inherited:

- **No mutable process-wide state.** Every `static` in `src/` is a method, `readonly`, or `const` —
  there are no mutable static fields for two pipelines to contend over.
- **`CompositionClaimRegistry(IServiceCollection services)` is per-collection**, holding an instance
  `_claims` list, so claim tracking is already scoped the way named pipelines need.

If either had been false, this design would not work at all.

### 2. Why sharing is explicit rather than inferred

The obvious rule — *services registered outside the block are shared, services registered inside are
per-pipeline* — *does not work*, and finding out why is the reason `AddRagNetShared` exists.

**The expensive services are registered inside the block.** `UseOnnxEmbeddings` does:

```csharp
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, OnnxEmbeddingGenerator>();
```

That is a **type** registration inside a `Use*` method. Four types hold an ONNX `InferenceSession` —
`OnnxEmbeddingGenerator`, `OnnxTokenEmbeddingGenerator`, `OnnxSpladeEncoder`, `OnnxReranker` — and
MiniLM alone is roughly 90 MB of model. Five named pipelines each calling `UseOnnxEmbeddings` would
load it five times.

Copying descriptors from the parent would not fix it either: an *instance* registration copies the
instance and shares, while a *type* registration constructs a second one. Same syntax, opposite
outcome, silently.

So sharing is stated once, in a block, using the same `Use*` methods and the same options validation
the caller already knows. Because `RagBuilder` is just a wrapper over a collection, that block is
three lines:

```csharp
public static IServiceCollection AddRagNetShared(this IServiceCollection services, Action<RagBuilder> configure)
{
    configure(new RagBuilder(services));
    return services;
}
```

**What it deliberately does not do is call `AddRagNETServices()` or register a pipeline.** Sharing a
model is not the same as running a pipeline in the parent, and a parent that registered one would
build its own stores alongside every child's — paying for a pipeline nobody asked for and muddying
which store a fallback resolves to.

**Automatic deduplication was rejected.** Hoisting identical descriptors across named blocks would
share by inference — two pipelines that happen to configure the same model would silently share a
session even where separation was the point. This project has spent the whole milestone finding
defects of exactly that shape.

### 3. Children are built lazily, and that is load-bearing

Child providers are built on **first `Get(name)`**, not at registration.

At registration time the parent provider does not exist, so there is nothing to fall back to. The
factory holds each name's `IServiceCollection` and builds once, under a lock, when first asked. With
a fixed startup-declared set this needs no eviction policy and no expiry.

**The cost, stated plainly:** a misconfigured named pipeline surfaces on first `Get()` rather than at
startup. That is a real regression against this project's house style, which validates eagerly at the
configuring line — `RagBuilder`'s own comment cites issue #90 for it.

It is accepted rather than solved because the alternative is worse: eager construction at
registration cannot see the parent, so it cannot share anything, which removes the feature's whole
point. An opt-in `validateOnStart` that builds every child immediately **after** the parent provider
exists is possible later and is explicitly left open; it is not in this phase.

### 4. Disposal is async, and ownership runs one way

`IRagPipelineFactory` is a singleton in the parent and implements **`IAsyncDisposable`**. Disposing
the parent container disposes the factory, which disposes each child provider, which disposes that
pipeline's stores.

**Async is not a stylistic choice**, though the original reason given here was overstated and is
corrected below.

`ServiceProvider.Dispose()` **throws** when it holds a service implementing *only*
`IAsyncDisposable`, with a message telling you to use `DisposeAsync`. Getting that wrong is a crash
at shutdown, not a leak.

**Corrected 2026-08-25.** An earlier draft said "seven types in the per-pipeline surface are
`IAsyncDisposable`", counting *interfaces* that declare it — `IBm25Index`, `IParentChunkStore`,
`IRagDataManager`, `ITagIndex`, `IGraphStore`, `IRaptorLeafStore`. That number is not the relevant
one: those interfaces also extend `IDisposable`, so their implementations dispose synchronously
without complaint.

**Exactly two concrete types implement `IAsyncDisposable` without `IDisposable`** — `SqliteAuditLog`
and `AzureServiceBusIngestionTrigger` — and they are the only ones that make synchronous disposal
throw. Fewer than claimed, but still reachable: a named pipeline calling `UseSqliteAuditLog` hits it.

Async disposal remains correct regardless — an async-disposable store should be closed
asynchronously whether or not skipping it would throw — but the justification is the two types, not
the seven interfaces.

**Shared services are disposed by the parent and never by a child.** Ownership runs one way, so
tearing down one named pipeline cannot pull the ONNX session out from under another.

## Testing

- **Two named pipelines write to different stores and cannot read each other's chunks.** The claim
  the feature exists to make; without it nothing here is verified.
- **A shared service is one instance across named pipelines** — assert reference equality of the
  embedder resolved from two children. This is the test that would catch descriptor-copying
  duplicating an ONNX session.
- **A per-pipeline service is not shared** — the converse, so the sharing test cannot pass by
  everything being shared.
- **Disposing the parent disposes every child's stores**, asserted on an `IAsyncDisposable` double,
  and **does not throw** — the `ServiceProvider.Dispose()` trap above.
- **Disposing does not dispose shared services twice.**
- **Unnamed `AddRagNet` is unchanged**, so the existing suite is itself the regression guard —
  including the documented root resolves (`IRagPipeline`, `IVectorStore`) that §1a protects.
- **The two forms coexist**: a root pipeline and a named one in the same container, each
  reaching its own store, with the shared embedder singular across both.

## Out of scope

- **Dynamic pipeline creation.** The set is declared at startup. Creation-on-demand needs an
  eviction or lifetime policy and thread-safe construction under contention; no one has asked for it.
- **`validateOnStart`.** Possible once the parent exists, deliberately deferred (§3).
- **Routing inside the vector store**, and **keyed `Use*` overloads** — both rejected above.
- **#353's auto-initialisation.** Phase 6.2.10, sequenced after this one precisely because this
  changes how stores are registered.
