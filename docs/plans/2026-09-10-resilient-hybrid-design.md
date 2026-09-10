# The Decorator That Hid a Capability — design for Phase 6.2.37

**Issue:** [#544](https://github.com/MarcelRoozekrans/Rag.NET/issues/544). **Filed** 2026-09-10 by
6.2.36, which found it while fixing #539 and deliberately shipped a diagnostic for it rather than a
fix.

## 0. What is wrong, and how long it has been wrong

`ResilientVectorStore` does not implement `IHybridSearchable`. `EnsembleBehavior` injects
`IVectorStore` — the **decorated** instance when resilience is registered — and probes
`VectorStore is IHybridSearchable`. That probe is `false` under resilience, so **native hybrid
dispatch never happens at all**, for every store that supports it, whenever `ConfigureResilience`
decorates the store.

This is not new. `IHybridSearchable` landed 2026-03-31 and `ResilientVectorStore` 2026-08-04, so the
defect has existed since the day the decorator was written — it predates the semantic ranker by four
months. The effect until 6.2.36 was a silent downgrade: correct results, one extra
backend round trip, client-side RRF instead of the backend's own fusion, and scores on a different
scale than the caller was told to expect. Nothing errored.

**6.2.36 made it worse in one specific way, which is why it is now scheduled.** The Azure semantic
ranker now lives on `HybridSearchAsync`. So registering `Rag.NET.Resilience` no longer merely costs
a round trip — it **silently disables semantic ranking**, on a path the caller opted into and paid
for. 6.2.36's refusal cannot catch that: the refusal is conditioned on
`VectorStore is IHybridSearchable`, which is precisely what the decorator makes false.

**The repository solved this exact problem once, for the sibling capability, and said why.**
`ResilientVectorStore.Create` returns a `ResilientSparseVectorStore` when the inner store is
`ISparseSearchable`:

> so an `is ISparseSearchable` probe on the resolved `IVectorStore` stays honest after decoration

The reasoning was applied to sparse and never to hybrid.

## 1. Two measurements that narrow the scope

Both were taken while writing this design rather than assumed, because the obvious framing — "the
decorator's capability model is broken" — turns out to be wrong.

**`ICollectionManageable` is never probed on a resolved `IVectorStore`.** Nothing in `src/` performs
`is ICollectionManageable` or `as ICollectionManageable`. It is registered as its own DI singleton by
each store's `Use*` extension and only ever resolved directly, where it correctly returns the
undecorated store. So the decorator dropping it is genuinely harmless, exactly as its class doc
claims. **`IHybridSearchable` is the only capability where probe-on-instance collides with
decoration.** This is one interface, not a model.

**Hybrid and sparse are disjoint today.** `AzureAISearchVectorStore` and `WeaviateVectorStore`
implement `IHybridSearchable`; neither implements `ISparseSearchable`. The sparse-capable stores —
`InMemoryVectorStore`, and the `QdrantSparseVectorStore` / `PgVectorSparseVectorStore` /
`PineconeSparseVectorStore` subclasses — implement no hybrid. So the combinatorial explosion
`ResilientVectorStore`'s own docs warn about is real in principle and **empty in practice**: a hybrid
variant needs no sparse×hybrid cross.

*(Both measurements were taken by reading the interfaces and every implementer, not by counting
declaration lines — a grep over `class … : IVectorStore` misses `QdrantSparseVectorStore` and its
two siblings, which inherit their base's interfaces.)*

## 2. Decided: a variant subclass, not implement-and-delegate

The codebase already argues **both** sides of this, and #544 is the case that has to pick one.
`ResilientVectorStore` handles its four interfaces two different ways:

| Pattern | Used for | Stated reason |
|---|---|---|
| Variant subclass, selected by `Create` | `ISparseSearchable` | "decorating a dense-only store must not make `store is ISparseSearchable` start returning `true`" |
| Implemented unconditionally, delegated, honesty kept by a support flag | `IChunkLookup`, `IScoreScaleAware` | "adding a variant per capability would need one class per combination — a sparse-and-lookup store, a lookup-only store, and so on" |

**Chosen: the variant.** `ResilientHybridVectorStore`, mirroring `ResilientSparseVectorStore`.

**Rejected: implement-and-delegate plus a `SupportsNativeHybrid` flag.** It would need no new class
ever, mirroring `IChunkLookup.SupportsChunkLookup`. But it changes the contract of a **public
interface that other stores implement**: every consumer moves from `is IHybridSearchable` to
`is IHybridSearchable { SupportsNativeHybrid: true }`, and an external implementer inherits a member
whose default must be `true` — the opposite polarity from `NativeOnlyCapability`, added to the same
interface the day before by 6.2.36, which defaults to `null` meaning *nothing declared*. Two adjacent
defaulted members with inverted polarity is a trap. With v1.0 the next milestone, leaving a public
interface untouched is worth a class.

**The combinatorial objection is weaker here than it looks**, and that is the deciding argument
rather than the general one. It applies to capabilities that are *orthogonal*. Hybrid and sparse are
not orthogonal in this pipeline: `EnsembleBehavior` treats a sparse arm that would run as a reason
**not** to dispatch natively (`SparseArmWouldRun` is one of the four blockers). A store that is both
would already take the client-side path, so the fourth class the objection predicts may never be
needed even if such a store appears.

**Rejected: unwrapping the decorator in `EnsembleBehavior`.** `IVectorStoreDecorator` exposes only
`InnerStoreType` and says why — "so no caller can reach around whatever behaviour the decorator
adds". Unwrapping would also dispatch the hybrid call *outside* the resilience pipeline, which
contradicts §4 directly. Recorded as rejected because it is the cheapest-looking option and will be
proposed again otherwise.

## 3. All three members must be forwarded, and this is the subtle part

`IHybridSearchable` carries three members. Two of them are defaulted, which means a variant that
forwards only the method still **compiles, passes an `is IHybridSearchable` probe, and dispatches
natively** — while silently answering for the backend on the other two.

| Member | Interface default | Why forwarding it matters |
|---|---|---|
| `HybridSearchAsync` | none | The dispatch itself. |
| `HybridScoreScale` | `ScoreScale.OpaqueRanking` | Defaulting it means the decorator declares a scale on the backend's behalf. Correct for both current implementers by luck, not by construction. |
| `NativeOnlyCapability` | `null` | **The one that bites.** Defaulting it means `EnsembleBehavior`'s refusal never fires under resilience — so a resilience user who enables semantic ranking and sets a `MinScore` gets correct, unranked results with no error. Native dispatch would be restored while the *guard on it* stayed broken. |

That last row is #544 reappearing one level in: a fix that restores the capability and leaves its
declaration hidden. **A partial fix that looks complete** is the failure family this milestone
exists to remove, so the mutation sweep must include a row that drops `NativeOnlyCapability`
forwarding and confirms something fails.

## 4. Native hybrid calls go through the resilience pipeline

Symmetric with `SearchAsync` and with `ResilientSparseVectorStore`, which retries its sparse
operations. A native hybrid query is a read against the same backend as the dense one, with the same
transient failure modes, and is idempotent in the same way.

**This contradicts a sentence in `ResilientVectorStore`'s class doc**, which must be rewritten rather
than amended:

> `ICollectionManageable` and `IHybridSearchable` are registered separately in DI by the store's own
> `Use*` extension and therefore resolve to the undecorated store — collection management and native
> hybrid search are not retried.

The sentence is not a decision that this phase overturns. It **described a consequence of the bug as
though it were a design choice** — native hybrid was "not retried" because it was not reached. The
`ICollectionManageable` half stays true and gains the reason §1 measured: nothing probes it on the
instance, so leaving it unforwarded costs nothing.

**This does change runtime behaviour for existing users** who have resilience registered and a
hybrid-capable store: their hybrid queries begin dispatching natively, on the backend's fusion scale,
through the retry pipeline. That is the fix working. It is called out here so the change is stated
rather than discovered.

## 5. `Create` refuses the combination it cannot represent

`Create` currently picks between two variants. It gains a third branch, and with it an ordering
question that is empty today (§1) and must not be answered silently tomorrow. A store that is both
`ISparseSearchable` and `IHybridSearchable` cannot be represented by either variant: choosing sparse
hides hybrid, choosing hybrid hides sparse — **both are #544 again**.

So `Create` throws `NotSupportedException` for that combination, naming the store type and both
capabilities, and saying a combined variant is required. A registration-time failure, not a query
that quietly does less than asked. This is the milestone's own rule applied to its own fix, and it
costs nothing today because no such store exists.

## 6. Scope

In:

1. **`ResilientHybridVectorStore`** — subclass, forwarding all three `IHybridSearchable` members
   through the pipeline (§3, §4).
2. **`ResilientVectorStore.Create`** — the new branch, and the `NotSupportedException` for the
   unrepresentable combination (§5).
3. **`ResilientVectorStore`'s class doc** — rewritten per §4.
4. **Tests** — registration-level in `Rag.NET.Resilience.Tests`, plus an `EnsembleBehavior` test
   proving native dispatch survives decoration.
5. **Docs** — `vector-stores.md`'s "do not combine resilience with semantic ranking" warning removed;
   `observability.md`'s capability-surface paragraph corrected a second time, to *forwarded and
   retried*; `retrieval.md`'s note that resilience keeps the client path removed.

Out:

- **6.2.36's `native_hybrid_hidden_by_decorator` warning stays.** The resilience case stops reaching
  it, but it is package-agnostic and catches any future decorator that hides the interface. Deleting
  it would close the hole for `ResilientVectorStore` and re-open it for everyone else.
- **`ICollectionManageable` stays unforwarded** (§1 measured why).
- **No change to `IHybridSearchable`** (§2).

## 7. Verifiability

**Fully verifiable locally, and that is worth stating** because the two phases before it were not.
Nothing here needs an Azure account: the decoration, the probe, the three delegations and the
`Create` branch are all exercised by in-process fakes and the existing DI tests. `EnsembleBehavior`'s
native dispatch is already tested against a fake hybrid store from 6.2.36.

The one thing local tests cannot show is that the backend's fused ranking is *better* than
client-side RRF — but that is not this phase's claim. Its claim is that the caller gets the path they
registered for, and that is entirely checkable.
