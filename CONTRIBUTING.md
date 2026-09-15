# Contributing to Rag.NET

Thanks for looking. This page covers the practical mechanics; if you only read one section, make it
the next one.

## The help we most need right now

**[#283 — re-record the connector cassettes against real services][283].** It needs ordinary accounts
on ordinary SaaS products, not .NET expertise, and one contribution is usually 20–40 minutes.

Rag.NET ships 19 connectors that talk to real services. Their tests replay WireMock cassettes, and an
audit on 2026-09-15 found that **38 of the 41 cassettes in this repository are hand-written** — fixtures
somebody wrote from reading the API docs. That verifies the code against *our belief about the API*
rather than against the API.

This is not a hypothetical risk. It has now cost us twice:

- A hand-written cassette agreed with a reranker defect that destroyed 26% of every document, and the
  tests stayed green.
- **`WebSearch.Tavily` shipped with broken authentication** ([#625], fixed in [#626]). It sent the key
  as an `api_key` body field Tavily had deprecated, with **no `Authorization` header at all**. Every
  test passed, because the hand-written cassette matched on path and method and therefore *could not
  observe a credential*. A unit test actively pinned the defect, asserting the deprecated field was
  populated — so a correct fix would have gone red and read as a regression.

Only the three GitHub cassettes are real recordings. Of all 41, exactly **four** have a matcher that
examines headers at all — those three, plus Tavily's, which was hardened after the defect above so
that a request without the bearer token no longer matches. Tavily was not unlucky; it was where a
defect happened to land in a blind spot the other 37 still share.

**Claim a service by commenting on [#283].** A recording that reveals a connector is broken is the
most valuable outcome, not a failed one — do not fix the cassette to match the code.

If you would rather not create an account for a service you do not use, that is fine and expected.
A package with no recording carries a `<VerifiedByReason>` in its csproj naming the service and what
stays unverified. Honest and machine-readable beats absent.

## Building and testing

```bash
dotnet build
```

**Do not use `dotnet test --filter`.** Every test project runs on Microsoft.Testing.Platform, which
ignores a VSTest filter — so that command used to run *every* test in the assembly while looking like
it ran one. The build now refuses it with `error RAGNET0001` rather than doing that silently, and the
error prints the replacement command with an absolute path you can paste.

Use the native xunit v3 runner, which honours `-class`, `-method` and `-filter`:

```bash
dotnet run --project tests/Rag.NET.Storage.Sqlite.Tests -- -class Rag.NET.Tests.Storage.SqliteCostLedgerTests
```

It also prints each test's **skip reason**, which `dotnet test` suppresses. Several suites are gated
on environment variables, and the skip reason names the variable to set.

### The test tiers

There are 79 test projects, partitioned into tiers that CI verifies are exhaustive — a project in no
tier fails the build rather than silently never running.

| Tier | What it needs | When it runs |
|---|---|---|
| fast | nothing | every push, both Linux and Windows |
| Docker | Docker; 12 projects marked `<RequiresDocker>true</RequiresDocker>` | every push, Linux only |
| nightly | LLM credentials | scheduled, Linux |

The Docker tier is Linux-only because Testcontainers uses Linux images, which Windows runners can host
only under nested virtualisation.

**Run every test project before pushing** — enumerate them rather than working from a list, and note
that `dotnet build` does not reach the packaging guards that `pack-validate` runs.

## Commits

Conventional commits, enforced by commitlint in CI over the commits a pull request adds. Allowed types
are the config-conventional set plus `bench`. Scopes are free-form.

Enable the local hook once per clone, which catches an over-length header before you push:

```bash
git config core.hooksPath .githooks
```

It checks exactly one rule — the 100-character header cap — deliberately, so it cannot drift from
commitlint. CI stays authoritative.

### One trap worth knowing about

**Do not put nested parentheses in a commit body.** release-please parses every commit with a
conventional-commit grammar, and a `(` inside an already-parenthesised group throws
`unexpected token '('` — at which point **the entire commit is silently dropped from the changelog**
while the workflow still reports success.

This has happened. A body line containing an attribute with a nested call was the only user-facing
change at the time, so no release PR was created at all and the fix shipped uncredited. Write the
symbol without parentheses, use a fenced code block, or restructure the sentence. Square brackets are
fine; it is the nested parens. After a `fix:` or `feat:` merges, confirm it appears in the
release-please PR — a green workflow does not mean the commit was counted.

## Pull requests

State what you changed and what you verified. Specifically:

- **What you measured, if you claim a performance change.** Change one variable per measurement, keep
  the build outside the timing, and give the median of several runs. A measurement that bundles two
  changes credits the wrong one — that happened here on [#640], where a bundled pragma took credit for
  a gain it did not produce, and the conclusion inverted once the two were separated.
- **What stays unverified.** An honest gap is more useful than an unstated one.
- **Which tests you ran**, given the tiers above.

If a guard blocks something you believe is correct, say so in the PR rather than working around it.
Several guards in this repository exist because a previous silent failure was expensive, and their
reasoning is written in the file next to them.

## Code of conduct

Be decent. Assume good faith, including when you are pointing out that something is broken —
especially then, since that is the contribution this project values most.

[283]: https://github.com/MarcelRoozekrans/Rag.NET/issues/283
[#283]: https://github.com/MarcelRoozekrans/Rag.NET/issues/283
[#625]: https://github.com/MarcelRoozekrans/Rag.NET/issues/625
[#626]: https://github.com/MarcelRoozekrans/Rag.NET/pull/626
[#640]: https://github.com/MarcelRoozekrans/Rag.NET/issues/640
