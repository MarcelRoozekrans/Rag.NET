# The Entry Point That Was Already Fluent — design for Phase 6.2.43

**Origin:** [#184](https://github.com/MarcelRoozekrans/Rag.NET/issues/184), filed 2026-08-12 as a
*design* task, labelled `breaking-change`. Scoped 2026-09-12.

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

## 2. What implementation must establish before writing the methods

Both of these can change what gets built, so they come first.

**2.1 — Is the ordering claim real?** Register the AI services *after* `AddRagNet` and assert the
resolved pipeline behaves identically to the documented order. A passing test deletes a documentation
sentence; a failing one reveals a real constraint that the new methods must respect.

**2.2 — What does `AddChatClient` do beyond registering?** If it wraps the client in middleware or
telemetry, a naive `Services.AddSingleton(client)` inside our builder method **silently loses that**,
and the loss would not show up in any test that only asserts the client resolves. The methods in §3
must therefore delegate to `AddChatClient` / `AddEmbeddingGenerator` rather than reimplement them, and
the plan must verify the delegation rather than assert it.

## 3. The two methods

On the **concrete `RagBuilder`**, not on `IRagBuilder`:

```csharp
public RagBuilder UseChatClient(IChatClient client)
public RagBuilder UseEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> generator)
```

**Why not `IRagBuilder`.** It is a shipped abstraction in `Rag.NET.Abstractions` with three members,
and external packages are generic over it. Adding members to it is a breaking change for any
implementer and buys nothing here: the `configure` callback hands the caller a `RagBuilder` already.

**Provider-agnostic by construction.** Both take the `Microsoft.Extensions.AI` abstractions this
library already consumes, so nothing new enters the dependency closure — the constraint Phase 4.6
established when it avoided a ~19 MB closure.

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

1. **`RagBuilder.UseChatClient`** and **`RagBuilder.UseEmbeddingGenerator`**, delegating to the
   `Microsoft.Extensions.AI` registrations.
2. **The ordering test** (§2.1) and the **delegation check** (§2.2).
3. **Chain-composition tests** in both directions (§3).
4. **The quickstart rewrite** and the ordering-sentence correction (§4).
5. **A comment on #184** recording what was found, including which of its premises no longer hold.

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

**§3's methods are directly testable**: register through the builder, resolve `IRagPipeline`, assert
the client and generator arrive. **§3's chaining claim is testable by compilation** — a test that
writes both orders is a compile-time assertion that the generic seam composes with the concrete
methods.

**§2.1 is the one that can invalidate its own premise**, which is why it is a test and not a
paragraph. **§2.2 is the one most likely to fail silently**: a test asserting "the client resolves"
passes whether or not `AddChatClient`'s wrapping survived, so the check must compare against what
`AddChatClient` itself produces rather than against a bare instance.

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
