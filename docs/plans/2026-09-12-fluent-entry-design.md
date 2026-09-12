# The Entry Point That Was Already Fluent — design for Phase 6.2.43

**Origin:** [#184](https://github.com/MarcelRoozekrans/Rag.NET/issues/184), filed 2026-08-12 as a
*design* task, labelled `breaking-change`. Scoped 2026-09-12.

## SCOPE REDUCED 2026-09-12, before any code was written

**The two builder methods are dropped. This phase ships the ordering test, the documentation
correction, and a comment on #184.**

The operator challenged the design as over-engineering. It does not survive the challenge, and the
reasoning is recorded here rather than quietly deleted, because the design reached §3 before anyone
asked the question.

**The methods unify syntax without reducing decisions.**

```csharp
// today
services.AddChatClient(chatClient);
services.AddEmbeddingGenerator(embedder);
services.AddRagNet(rag => rag.UsePgVector(conn, 1536));

// as designed
services.AddRagNet(rag => rag
    .UseChatClient(chatClient)
    .UseEmbeddingGenerator(embedder)
    .UsePgVector(conn, 1536));
```

Same objects constructed, same three things the caller must know exist. **The verbose part was never
the registration** — it is `new OpenAIClient(key).GetChatClient("gpt-4o").AsIChatClient()`, unchanged
in both. The operator's stated goal was *"fewest decisions to something working"*, and **the decision
count is identical**. Only the punctuation moved.

**And the cost was real.** A `Microsoft.Extensions.AI` reference on core, and — decisively — **two ways
to register the same service**, with a new question attached: *do I call `AddChatClient` or
`UseChatClient`?* §3 originally rejected the no-dependency variant partly for laying that trap, then
chose an option that lays it too. That is an inconsistency in the reasoning, not a nuance.

**What survives is the half this document treated as a footnote:** a documentation sentence that
probably states a constraint which does not exist, and an issue whose premise has drifted. Both are
worth fixing, and neither needs an API.

**The dependency question disappears with the methods.** §2.2's finding stands as a correct and useful
piece of knowledge — `AddChatClient` is a pipeline entry point returning a `ChatClientBuilder`, not a
registration helper — but it no longer decides anything here. It is kept because it is the reason a
naive implementation would have been wrong, and the next person to propose this will need it.

## 0. The correction this phase opens with

**#184's premise has drifted, and the drift is the most useful thing to record.** The issue describes
bootstrapping as *"knowing which of several extension methods to call, across several packages, in the
right order"*, and asks for *"one builder where everything is configured fluently"*.

**The builder exists and the documented quickstart is already fluent:**

```csharp
services.AddRagNet(rag => rag
    .UsePgVector("Host=localhost;Database=ragdb;Username=postgres;Password=secret",
                 vectorDimensions: 1536)
    .AddPdfParser()
    .AddParser<MyCustomParser>());
```

Optional packages attach through `TBuilder where TBuilder : IRagBuilder` returning `TBuilder`, which is
what makes that chain compose across package boundaries without core knowing they exist.

**Three of #184's supporting claims were checked and two no longer hold:**

| Claim in #184, 2026-08-12 | Status, 2026-09-12 |
|---|---|
| *"`refactor(graph)!` is already in flight (#181), so the bump is happening regardless"* | **#181 is merged.** That argument is gone |
| *"#161 — related, worth designing together"* | **#161 is closed** |
| *"75 classes match `*Extensions`"* | 78 declarations, **53 distinct class names** |

**A methodological note, because it changed the scope.** The extension surface was first counted by
grep, and two reasonable-looking greps returned **42** and **3** for the same quantity — C# signatures
wrap across lines, so neither a `this X` search nor a `(\s*this X` search sees the real first
parameter. The numbers above come from reading `IRagBuilder`, `RagBuilder` and
`ServiceCollectionExtensions` instead. **No count in this document should be treated as exact**, and
the implementation plan must not derive work from one.

## 1. What actually remains

**One seam.** The model and the embedder are registered *outside* the chain, as two
`Microsoft.Extensions.AI` calls, before it:

```csharp
services.AddChatClient(new OpenAIClient("sk-...").GetChatClient("gpt-4o").AsIChatClient());
services.AddEmbeddingGenerator(
    new OpenAIClient("sk-...").GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());

services.AddRagNet(rag => rag.UsePgVector(…));
```

That is the only point at which a quickstart caller leaves the fluent surface. There is no
`UseChatClient` or `UseEmbeddingGenerator` on `RagBuilder` — checked directly; the seam is absent
rather than differently named.

**And the stated reason for the ordering may not exist.** `docs/getting-started.md` says *"Register
them before calling `AddRagNet`"*. Every consumption found is `sp.GetService` or
`sp.GetRequiredService` **inside a factory lambda** — resolution time, not registration time — which
would make DI order irrelevant. **This is stated as a hypothesis, not a finding**, and §2 turns it
into a test. If it is true, that sentence has been teaching a constraint that does not exist.

## 2. What implementation must establish

**Only §2.1 remains in scope.** §2.2 is retained as a finding, not as a task — see the
scope-reduction section at the top.

Both of these can change what gets built, so they come first.

**2.1 — Is the ordering claim real?** Register the AI services *after* `AddRagNet` and assert the
resolved pipeline behaves identically to the documented order. A passing test deletes a documentation
sentence; a failing one reveals a real constraint that the new methods must respect.

**2.2 — ANSWERED 2026-09-12, before planning, and it changed the design.** `AddChatClient` is **not a
registration helper — it is a pipeline entry point.** It returns a `ChatClientBuilder`, and the same
assembly carries `UseLogging`, `UseOpenTelemetry`, `UseDistributedCache` and `UseFunctionInvocation`.
A naive `Services.AddSingleton(client)` would therefore discard the entire middleware surface, and no
test asserting "the client resolves" would notice. The methods in §3 delegate, and the plan verifies
the delegation rather than asserting it.

**This falsified §3's original dependency claim.** `AddChatClient` and `AddEmbeddingGenerator` live in
**`Microsoft.Extensions.AI`**; `src/Rag.NET/Rag.NET.csproj` references only
**`Microsoft.Extensions.AI.Abstractions`**. Delegating and leaving the closure untouched are mutually
exclusive, and this document originally asserted both.

**Measured cost of the reference, rather than estimated:** one **~518 KB** assembly for `net10.0`. Its
dependencies are `Microsoft.Extensions.{Caching,Logging,DependencyInjection}.Abstractions`, which core
already carries; `System.Text.Json`, `System.Threading.Channels` and `System.Diagnostics.DiagnosticSource`,
framework-provided on `net10.0`; and `System.Numerics.Tensors`, the one genuinely additional package.
Nothing resembling the ~19 MB closure Phase 4.6 avoided.

**A methodology note, because it nearly produced a false finding.** The first attempt to locate these
symbols used `strings`, which is **not installed on this machine**. It reported zero matches for every
symbol, which reads exactly like proof of absence. The figures above come from `grep -a` against the
assemblies.

## 3. The two methods — SUPERSEDED, NOT BUILT

**This section is kept as the record of a design that was scoped out before implementation, and of
why. Nothing in it ships.** It is left in place rather than deleted because the next person to
propose an entry-point method will arrive at the same shape, and the reason it was rejected is more
useful than its absence.

On the **concrete `RagBuilder`**, not on `IRagBuilder`:

```csharp
public RagBuilder UseChatClient(IChatClient client)
public RagBuilder UseEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
```

**Why not `IRagBuilder`.** It is a shipped abstraction in `Rag.NET.Abstractions` with three members,
and external packages are generic over it. Adding members to it is a breaking change for any
implementer and buys nothing here: the `configure` callback hands the caller a `RagBuilder` already.

**Provider-agnostic, but NOT closure-free — corrected 2026-09-12.** Both take the
`Microsoft.Extensions.AI` abstractions this library already consumes, so no *provider* package enters
the closure and Phase 4.6's constraint holds in the sense that mattered. **But core does take a new
reference on `Microsoft.Extensions.AI` itself**, ~518 KB, because that is where `AddChatClient` lives.
This document originally claimed the closure was untouched; that was wrong, and §2.2 records how it
was found.

**Decided 2026-09-12, by the implementer rather than the operator, who expressed no preference.** The
alternative was registering directly with `Services.AddSingleton` and taking no reference — but that
ships a method which silently differs from the standard registration, discarding the middleware
surface while looking equivalent. **That is the trap family this milestone keeps removing**, and
~518 KB is a proportionate price for not laying a new one. Reversible by moving the methods to a
package that already references `Microsoft.Extensions.AI`.

**The signature therefore carries the builder**, so middleware stays reachable through the fluent
surface rather than being amputated by it:

```csharp
public RagBuilder UseChatClient(IChatClient client, Action<ChatClientBuilder>? configure = null)
```

**Chaining composes in both directions, and this is a property to test rather than assume.**
`UseChatClient` returns `RagBuilder`; the package extensions are generic on `TBuilder : IRagBuilder`
and return `TBuilder`. So both of these compile:

```csharp
rag.UseChatClient(c).UsePgVector(…)
rag.UsePgVector(…).UseChatClient(c)
```

The quickstart collapses from three statements to one:

```csharp
services.AddRagNet(rag => rag
    .UseChatClient(new OpenAIClient(key).GetChatClient("gpt-4o").AsIChatClient())
    .UseEmbeddingGenerator(
        new OpenAIClient(key).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator())
    .UsePgVector(connectionString, vectorDimensions: 1536));
```

## 4. The documentation correction

`docs/getting-started.md` gains the single-statement form, and the *"Register them before calling
`AddRagNet`"* sentence is corrected or deleted **according to what §2.1 proves, not according to this
document's expectation**.

`DocsCodeExamplesTests` already resolves every type named in a `docs/` example against the shipped
assemblies, so a rewritten quickstart is checked rather than merely plausible.

## 5. Scope

In:

1. **The ordering test** (§2.1) — register the AI services *after* `AddRagNet` and assert the
   resolved pipeline behaves identically.
2. **The ordering-sentence correction** in `docs/getting-started.md` (§4), according to what the test
   proves rather than what this document expects.
3. **A comment on #184** recording what was found: that the builder is already fluent, which of its
   premises no longer hold, and why the entry-point methods were scoped out.

Out, in addition to everything below:

- **`RagBuilder.UseChatClient` / `UseEmbeddingGenerator`** and the `Microsoft.Extensions.AI` reference
  they would require. Dropped with reasons, see the scope-reduction section at the top.

Out:

- **Provider-specific `UseOpenAI(key)` / `UseOllama(url)`.** Core cannot reference provider packages,
  so each would land in its own package and give Rag.NET provider-shaped API surface it does not own.
  Rejected explicitly, not overlooked.
- **Removing, renaming or changing any existing extension method.** This phase is additive:
  **every call site that compiles today still compiles.** Despite #184's `breaking-change` label,
  nothing here is breaking.
- **Adding members to `IRagBuilder`.**
- **The options-discoverability layer** — exposing `RetrievalOptions`, `RagOptions` and
  `IngestionOptions` through the builder. A real idea, deliberately not this phase: the operator chose
  "fewest decisions to something working" over "make the whole surface discoverable", and building
  both would be the larger design #184 originally implied.
- **Anything about named pipelines** or the `AddRagNet(name, …)` overload.

## 6. Verifiability

**§2.1 is the whole of this phase's verification, and it can invalidate its own premise** — which is
why it is a test rather than a paragraph. Registering the AI services after `AddRagNet` either
resolves identically, in which case a documentation sentence is deleted, or it does not, in which case
the sentence is correct and the phase has found a real constraint worth documenting properly.

**Either outcome is a result.** The test is not written to confirm the expectation; it is written
because the expectation rests on reading factory lambdas, and reading is what produced two
contradictory extension counts in §0.

**What this phase cannot establish** is whether the remaining sprawl matters to anyone. The 78
extension declarations stay exactly where they are; this adds one small surface at the one point the
quickstart left the chain. If callers are in fact confused by the breadth of `*Extensions` classes,
that is a separate finding needing separate evidence, and it should be gathered from users rather than
assumed from a count — particularly given §0's note about what counting produced here.

## 7. What this means for #184

**If §2 confirms the gap is two calls and one incorrect sentence, #184 asks for a redesign of
something already substantially fluent.** The phase should say so on the issue — with the evidence,
and with its two falsified premises named — rather than closing it quietly as though the original
scope had been delivered.

That matters beyond this issue: #184 is labelled `breaking-change` and was the strongest remaining
argument for doing breaking work before v1.0 tags. **If it closes additively, that argument dissolves,
and Milestone 6's remaining locally-finishable work no longer has a deadline attached to the release.**
Whoever plans the next phase should know that.
