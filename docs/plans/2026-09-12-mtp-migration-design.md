# The Migration a Dependency Bump Was Hiding — design for Phase 6.2.41

**Origin:** [#314](https://github.com/MarcelRoozekrans/Rag.NET/pull/314), a Renovate PR open since
2026-08-18 with three checks red. Diagnosed 2026-09-12.

## 0. What #314 actually is

A one-line version bump — `xunit.v3` 3.2.2 → 4.0.0 in `Directory.Packages.props` — with **one root
cause across all three failing checks**, and no xunit API involved:

> Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and
> later. If you use dotnet test, you should opt-in to the new dotnet test experience.
> — `Microsoft.Testing.Platform.MSBuild/2.3.3/…/Microsoft.Testing.Platform.MSBuild.targets(320,5)`

`xunit.v3` 4.0.0 pulls `Microsoft.Testing.Platform.MSBuild` **2.3.3**, which on the .NET 10 SDK
refuses the VSTest bridge outright. Every test project fails to *build* its VSTest target, before a
test runs. **Renovate can never make this green**, and the migration it implies is worth doing
independent of xunit v4: the deadline belongs to whichever MTP version this repository lands on, and
staying on v3 defers rather than avoids it.

## 1. The scale, and a correction to what was first reported

**The diagnosis comment on #314 called this a "78-project test-runner migration" and a "large phase".
That overstated it.**

`tests/Directory.Build.props` exists and is imported by every test project. The opt-in is a property:

```xml
<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
```

Set there once, it applies to all 78. The migration is **one property in one shared file**, not 77
`.csproj` edits — plus whatever behaviour follows, which §3 argues is the actual work.

Counts, measured rather than estimated: **78 test projects** (`tests/Rag.NET.Testing` is a shared
helper library with no `Microsoft.NET.Test.Sdk` and is correctly outside the census), of which
**exactly one** already opts in.

## 2. Incremental migration is not an open question — it is the status quo

The #314 comment said whether this could go incrementally "is not obvious from here". **It is
obvious once the existing MTP project is read, and the answer is yes, already proven in production.**

`tests/Rag.NET.Benchmarks.Quality.IntegrationTests` has set
`TestingPlatformDotnetTestSupport` since #275. It declares `RequiresSecrets` — an overlay, not a tier
— and neither `RequiresDocker` nor `RequiresLlm`, so it sits in the **fast tier that `ci.yml` runs on
every pull request**, one project at a time via `dotnet test --no-build`, alongside 65 VSTest
projects.

**So mixed VSTest/MTP mode has been running on every PR for weeks.** And the project keeps
`Microsoft.NET.Test.Sdk` deliberately — its own csproj comment says *"Microsoft.NET.Test.Sdk stays
referenced because both workflows SELECT"* by it. That matters more than it looks: `ci.yml`'s
partition guard computes its census with `grep -l 'Microsoft.NET.Test.Sdk' tests/*/*.csproj`, so a
migration that removed the reference would drop projects out of the census **while the guard still
passed**, because `all` shrinks alongside `fast` and `docker`. The precedent to follow is the one
already set: opt in, keep the reference.

## 3. The real consequence: `dotnet test --filter` starts erroring everywhere

`Directory.Build.targets` — phase 6.2.35, issue #529 — raises `error RAGNET0001` when a VSTest filter
is passed to a project MTP runs. It hooks `BeforeTargets="InvokeTestingPlatform"`, a target that
exists only under MTP, and its comment states the intent explicitly:

> this guard ARMS ITSELF for any project that adopts MTP later, including by a route other than that
> property

So the moment the property lands in `tests/Directory.Build.props`, **`dotnet test --filter X` becomes
an error on every test project in the repository.** That is correct — the filter was silently ignored
before, running every test in the assembly while appearing to narrow — and the guard already emits a
copy-pasteable replacement naming the native runner and `-class`.

**This is the migration's felt cost, and it lands on developers rather than on CI.** `ci.yml` and
`nightly.yml` select by project and pass no filter, so they are unaffected. Every human who types
`dotnet test --filter` is affected immediately. 6.2.35 built precisely the right guard for a migration
that had not been scheduled when it shipped, which is the one piece of luck in this phase.

**The phase must therefore document the new local workflow**, not merely flip the property. A
migration that silently changes how every contributor runs a single test is the same failure family
this milestone keeps removing, one layer up.

## 4. What implementation must verify rather than assume

- **That the property works from `tests/Directory.Build.props`.** MSBuild property scope and the MTP
  targets' read order should make it fine, but the existing precedent sets it in a `.csproj`, and
  "should be fine" is how the last two phases acquired their corrections.
- **That the already-migrated project's own setting becomes redundant** and whether to remove it.
  Leaving it is harmless; removing it loses the comment explaining why `Microsoft.NET.Test.Sdk` stays.
  Prefer keeping the comment.
- **That every one of the 78 projects still runs**, project by project, and reports the same counts as
  before. A runner migration that silently stops executing a project's tests is exactly the defect
  `ci.yml`'s assembly pre-check exists for, and that check was written against VSTest semantics.
- **That `pack-validate` and the nightly workflow survive**, both of which invoke `dotnet test`.
- **Whether the xunit v4 bump is part of this phase or the one after.** Migrating first and bumping
  second keeps two failure sources apart; bumping in the same change proves the migration was
  sufficient. The plan should choose deliberately and say why.

## 5. Scope

In:

1. **Opt into MTP** for all test projects, via `tests/Directory.Build.props`.
2. **Verify every project still runs** with unchanged counts (§4).
3. **Document the local workflow change** — `--filter` now errors; the native runner is the
   replacement — wherever contributors are told how to run tests.
4. **Adopt the xunit v4 bump and close #314**, if §4's last question resolves that way.

Out:

- **Any test rewrite.** No xunit API broke; this is a runner change.
- **Touching `Directory.Build.targets`' guard.** Its comment explicitly forbids the tidy a migrator is
  most likely to attempt, and it is already correct for the post-migration world.
- **Widening the `RequiresLlm` / `RequiresDocker` tiers**, or changing the partition guard. The census
  survives if the `Microsoft.NET.Test.Sdk` reference stays (§2), and it should.

## 6. Verifiability

**Unusually well covered for a phase this structural**, because the repository already guards the
failure modes: `ci.yml`'s partition check catches a project falling out of every tier, its
assembly-existence pre-check catches a project whose tests silently do not run, and
`Rag.NET.RepoConventions.Tests` asserts tier-marker invariants.

**What no guard covers is the aggregate**: that the *same number of tests* ran after the migration as
before. The honest mitigation is to record per-project counts before and compare after — and to state
the totals in the phase record, so a later reader can tell whether the migration preserved coverage or
merely stayed green.
