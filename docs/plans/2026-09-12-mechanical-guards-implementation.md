# Three Mechanical Guards Implementation Plan — Phase 6.2.42

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn rules this repository wrote down and then broke into checks that fire by themselves — a
commit-header hook, an informative BEIR skip message, and a CI job that names the test that failed.

**Architecture:** Three independent guards sharing no code. Guard C edits four identical shell loops
in two workflow files. Guard A adds a tracked `git` hook plus a test that executes it. Guard B changes
two skip *messages* and no skip *conditions*. Nothing in `src/` changes behaviour; the only `src/`
edit is one new static helper in a non-packable benchmark project.

**Tech Stack:** .NET 10, xunit v3 on Microsoft.Testing.Platform, GitHub Actions, POSIX shell,
commitlint 21.

**Spec:** `docs/plans/2026-09-12-mechanical-guards-design.md`

## Global Constraints

Copied from the design and this repository's standing rules. Every task's requirements implicitly
include this section.

- **Commit headers are capped at 100 characters**, and CI lints every commit a PR adds — not just the
  tip. A long header on a non-tip commit costs a branch rebuild, not an amend.
- **No nested parentheses inside a parenthesised group in a commit body.** release-please parses every
  commit and silently drops the whole commit on `unexpected token '('`.
- **Never write a `claude.ai/code/session_*` URL** into a commit message or PR body.
- **Do not reimplement commitlint locally.** Guard A is header length only. `.commitlintrc.yml` tunes
  `type-enum` with `bench`, sets `subject-case` to `0`, and sets `body-max-line-length` to `0` because
  bodies quote URLs verbatim. CI stays authoritative.
- **Guard B changes no skip conditions.** The same tests skip under the same environment.
- **Guard C changes no test selection and no failure determination.** It prints on the failure path.
- **`docs/plans/` is where design and plan records live** — not `docs/superpowers/plans/`.
- **XML and MSBuild comments must not contain a double hyphen.** `--` is `error MSB4024` and breaks
  every project importing the file. This has bitten twice.
- **Enumerate test suites as literal commands; never reason about which are safe to skip.**
- **`dotnet test --filter` raises `RAGNET0001` everywhere** since phase 6.2.41. Select with the native
  runner: `-class "*TypeName"` or `-method "*MethodName*"`.

---

## File Structure

| File | Responsibility | Guard |
|---|---|---|
| `.github/workflows/ci.yml` | fast-tier and Docker-tier loops dump the failure log | C |
| `.github/workflows/nightly.yml` | LLM-tier and its second loop dump the failure log | C |
| `tests/Rag.NET.RepoConventions.Tests/CiFailureReportingTests.cs` | **new** — pins the dump into all four loops | C |
| `.githooks/commit-msg` | **new** — rejects a header over 100 characters | A |
| `tests/Rag.NET.RepoConventions.Tests/CommitMessageHookTests.cs` | **new** — executes the hook, asserts its exits | A |
| `docs/reference/ci.md` | documents the one-time `core.hooksPath` line | A |
| `src/Rag.NET.Benchmarks.Quality/BeirDatasetCache.cs` | **new static method** naming the conventional cache | B |
| `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs` | `SkipReason` gains the hint | B |
| `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/SkipMessageTests.cs` | **new** — pins the message | B |
| `tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs` | its own `SkipReason` gains the hint | B |
| `tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs` | its own `SkipReason` gains the hint | B |

**Guard C is Task 1 deliberately.** It is the most valuable of the three, and once it is in place the
failures of Tasks 2 and 3 are legible instead of anonymous.

---

### Task 1: Guard C — make a red CI job name the test that failed

**Files:**
- Modify: `.github/workflows/ci.yml` — the `dotnet test` line inside two loops
- Modify: `.github/workflows/nightly.yml` — the `dotnet test` line inside two loops
- Test: `tests/Rag.NET.RepoConventions.Tests/CiFailureReportingTests.cs` (create)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: nothing later tasks depend on. `TestProject.FindRepositoryRoot()` is an existing helper in
  `tests/Rag.NET.RepoConventions.Tests/TestProject.cs` returning the absolute repository root.

**Background the implementer needs.** Microsoft.Testing.Platform prints only a count on failure —
`Failed! - Failed: 1, Passed: 81, …` — and writes the test name, assertion and stack to
`<project-dir>/bin/Release/<tfm>/TestResults/<AssemblyName>_<tfm>_<arch>.log`. Nothing uploads that
file, so it dies with the runner. **UPDATED 2026-09-12, closed by evidence rather than left open: it
is UTF-16LE with a `FF FE` BOM on both Windows and Linux**, verified twice during Task 1 — by the
implementer and independently by the re-reviewer — each in a fresh
`mcr.microsoft.com/dotnet/sdk:10.0` container with no reused Windows build output. `cat` prints
spaced-out mojibake on both platforms; the code below sniffs the BOM and decodes with `iconv` on
every platform it has been checked on, so the `iconv` branch fires unconditionally and the `else: cat`
branch is dead code kept only as a fallback should some future runner disagree.

All four loops contain this line **byte-identically**, at 12 spaces of indentation:

```
            dotnet test "$project" --no-build -c Release --verbosity normal || failed="$failed $project"
```

- [ ] **Step 1: Write the failing test**

Create `tests/Rag.NET.RepoConventions.Tests/CiFailureReportingTests.cs`:

```csharp
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Pins the failure-log dump into every workflow loop that runs tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Phase 6.2.41 moved every test project onto Microsoft.Testing.Platform,
/// which prints a failure <i>count</i> and nothing else — no test name, no assertion, no stack. The
/// first red build after that migration was #571, where diagnosing one flaky test took a full log
/// read, a count comparison against main, and a reproduction outside the repository, and the failing
/// test still could not be named.
/// </para>
/// <para>
/// A dump is exactly the kind of change that reviews well and silently stops working — a renamed
/// directory, a restructured loop, a tidy-up that drops it. This pins it to all four loops rather
/// than trusting that nobody removes it.
/// </para>
/// </remarks>
public sealed class CiFailureReportingTests
{
    private static readonly string[] WorkflowsThatRunTests = ["ci.yml", "nightly.yml"];

    /// <summary>Every loop that runs tests must also dump the log when a project fails.</summary>
    [Fact]
    public void EveryWorkflowLoopThatRunsTestsDumpsTheFailureLog()
    {
        foreach (var workflow in WorkflowsThatRunTests)
        {
            var path = Path.Combine(
                TestProject.FindRepositoryRoot(), ".github", "workflows", workflow);
            var text = File.ReadAllText(path);

            var runs = CountOccurrences(text, "dotnet test \"$project\" --no-build -c Release");
            var dumps = CountOccurrences(text, "TestResults/*.log");

            Assert.True(
                runs > 0,
                $"{workflow} no longer runs tests with the pinned command, so this guard is " +
                "asserting nothing. Either the workflow changed shape or this test is stale.");

            Assert.True(
                dumps >= runs,
                $"{workflow} runs `dotnet test` in {runs} place(s) but dumps the failure log in " +
                $"{dumps}. Microsoft.Testing.Platform prints only a failure count, so a loop " +
                "without the dump produces a red job that names no test at all — see #571.");
        }
    }

    /// <summary>The dump must decode UTF-16 rather than printing it as bytes.</summary>
    [Fact]
    public void TheDumpDecodesTheLogRatherThanPrintingItRaw()
    {
        foreach (var workflow in WorkflowsThatRunTests)
        {
            var text = File.ReadAllText(Path.Combine(
                TestProject.FindRepositoryRoot(), ".github", "workflows", workflow));

            Assert.Contains("iconv -f UTF-16LE", text, StringComparison.Ordinal);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`

Expected: **FAIL**, both tests. `EveryWorkflowLoopThatRunsTestsDumpsTheFailureLog` reports
`ci.yml runs dotnet test in 2 place(s) but dumps the failure log in 0`.

- [ ] **Step 3: Replace the run line in all four loops**

In **both** `.github/workflows/ci.yml` and `.github/workflows/nightly.yml`, replace every occurrence
of this line:

```
            dotnet test "$project" --no-build -c Release --verbosity normal || failed="$failed $project"
            echo "::endgroup::"
```

with this block, preserving the same 12-space indentation:

```
            if dotnet test "$project" --no-build -c Release --verbosity normal; then
              echo "::endgroup::"
              continue
            fi
            failed="$failed $project"
            # Microsoft.Testing.Platform prints the failure COUNT and nothing else: no test name, no
            # assertion, no stack. All of it goes to a per-project log that nothing uploads, so
            # before this dump a red job named no test at all. Phase 6.2.42; see #571.
            # The file is UTF-16LE with a BOM on Windows, where cat prints spaced-out mojibake, so
            # sniff the BOM and decode rather than assuming an encoding.
            for log in "$(dirname "$project")"/bin/Release/*/TestResults/*.log; do
              [ -f "$log" ] || continue
              echo "----- failure detail from $log -----"
              if [ "$(head -c 2 "$log" | od -An -tx1 | tr -d ' ')" = "fffe" ]; then
                iconv -f UTF-16LE -t UTF-8 "$log" || cat "$log"
              else
                cat "$log"
              fi
            done
            echo "::endgroup::"
```

Note that the original trailing `echo "::endgroup::"` is **consumed by the replacement** — the block
above emits it on the success path via `continue` and again after the dump. Re-read each loop
afterwards and confirm exactly one `::endgroup::` is reached on each path; two would break the log
grouping.

There are **four** occurrences: `ci.yml` fast tier, `ci.yml` Docker tier, `nightly.yml` LLM tier,
`nightly.yml` second loop. Verify counts before and after:

```bash
grep -c 'dotnet test "$project" --no-build' .github/workflows/ci.yml .github/workflows/nightly.yml
grep -c 'TestResults/\*\.log' .github/workflows/ci.yml .github/workflows/nightly.yml
```

Both must report `2` for each file.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`

Expected: **PASS**, and the project's passed count rises by 2 from its 101 baseline.

- [ ] **Step 5: Prove the dump actually works, by making a test fail on purpose**

**This step is the difference between shipping a guard and shipping a file that looks like one, and it
is not optional.** A green run exercises none of this — which is exactly how 6.2.41 shipped the
regression this task fixes.

Create a scratch project outside the repository:

```bash
SP="$(mktemp -d)"
mkdir -p "$SP/probe" && cd "$SP/probe"
cat > probe.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.2" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
  </ItemGroup>
</Project>
EOF
cat > Tests.cs <<'EOF'
using Xunit;
public class Probe
{
    [Fact] public void ThisOneFailsOnPurpose() => Assert.Equal("marker-ALPHA", "marker-BETA");
}
EOF
dotnet test -c Release --verbosity normal > console.txt 2>&1 || true
```

Confirm the console says nothing useful, which is the defect being fixed:

```bash
grep -c "ThisOneFailsOnPurpose\|ALPHA\|BETA" console.txt    # expected: 0
```

Now run the dump logic exactly as the workflow will:

```bash
for log in ./bin/Release/*/TestResults/*.log; do
  [ -f "$log" ] || continue
  if [ "$(head -c 2 "$log" | od -An -tx1 | tr -d ' ')" = "fffe" ]; then
    iconv -f UTF-16LE -t UTF-8 "$log" || cat "$log"
  else
    cat "$log"
  fi
done | grep -E "^failed |Assert|Expected|Actual"
```

Expected output — the test name and the assertion, in readable text:

```
failed Probe.ThisOneFailsOnPurpose (118ms)
  Assert.Equal() Failure: Strings differ
  Expected: "marker-ALPHA"
  Actual:   "marker-BETA"
```

**Record this output verbatim in the pre-push review.** If it prints spaced-out single characters,
the BOM branch is wrong and must be fixed before committing. Then `cd` back to the repository and
`rm -rf "$SP"`.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/nightly.yml \
        tests/Rag.NET.RepoConventions.Tests/CiFailureReportingTests.cs
git commit -m "ci: dump the failure log so a red job names the test that failed (#571)"
```

---

### Task 2: Guard A — a commit-header hook, and a test that runs it

**Files:**
- Create: `.githooks/commit-msg`
- Create: `tests/Rag.NET.RepoConventions.Tests/CommitMessageHookTests.cs`
- Modify: `docs/reference/ci.md` — add the one-time setup line

**Interfaces:**
- Consumes: `TestProject.FindRepositoryRoot()`.
- Produces: `.githooks/commit-msg`, invoked as `<bash> .githooks/commit-msg <path-to-message-file>`,
  exiting `0` when the first non-comment line is at most 100 characters and `1` otherwise.

**The trap this task must not fall into.** This repository's commit headers routinely contain em
dashes — `docs(plans): scope 6.2.41 — the migration a dependency bump was hiding`. An em dash is one
character but **three bytes** in UTF-8. A hook that counts bytes rejects valid headers, and the
rejection looks like a correct guard doing its job. `commitlint` counts characters. The test below
pins this with a 100-character em-dash header that must be **accepted**.

- [ ] **Step 1: Write the failing test**

Create `tests/Rag.NET.RepoConventions.Tests/CommitMessageHookTests.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Executes <c>.githooks/commit-msg</c> and asserts what it accepts and rejects.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hook is run, not read.</b> A test that greps the script for "100" would pass against a
/// hook that never rejects anything. This phase exists because rules that are written down are not
/// checked at the moment they apply, and a guard nobody executes is the same failure one level up.
/// </para>
/// <para>
/// <b>What this cannot test is adoption.</b> The hook does nothing until someone runs
/// <c>git config core.hooksPath .githooks</c> in their clone. That is a stated cost of the chosen
/// delivery mechanism, recorded in the design, not an oversight here.
/// </para>
/// </remarks>
public sealed class CommitMessageHookTests
{
    private const int Cap = 100;

    /// <summary>A header one character over the cap must be rejected.</summary>
    [Fact]
    public void AHeaderOverTheCapIsRejected()
    {
        var header = "chore: " + new string('x', Cap + 1 - "chore: ".Length);
        Assert.Equal(Cap + 1, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode != 0,
            $"The hook accepted a {header.Length}-character header. commitlint caps headers at " +
            $"{Cap} and lints every commit a pull request adds, so this one fails CI after the " +
            "push — which is the cost the hook exists to remove.");

        Assert.Contains(Cap.ToString(CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
    }

    /// <summary>A header exactly at the cap must be accepted.</summary>
    [Fact]
    public void AHeaderExactlyAtTheCapIsAccepted()
    {
        var header = "chore: " + new string('x', Cap - "chore: ".Length);
        Assert.Equal(Cap, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode == 0,
            $"The hook rejected a header of exactly {Cap} characters, which commitlint accepts. " +
            $"Output: {output}");
    }

    /// <summary>
    /// A 100-character header containing em dashes must be accepted — the byte-counting trap.
    /// </summary>
    /// <remarks>
    /// An em dash is one character and three UTF-8 bytes, and this repository's headers use them.
    /// A hook measuring bytes rejects this header while reporting a length of 104, which looks
    /// exactly like a correct guard.
    /// </remarks>
    [Fact]
    public void AHeaderAtTheCapContainingEmDashesIsAccepted()
    {
        const string prefix = "docs(plans): scope — a phase — with em dashes ";
        var header = prefix + new string('x', Cap - prefix.Length);
        Assert.Equal(Cap, header.Length);

        var (exitCode, output) = RunHook(header);

        Assert.True(
            exitCode == 0,
            "The hook rejected a 100-CHARACTER header containing em dashes, so it is counting " +
            $"bytes rather than characters. commitlint counts characters. Output: {output}");
    }

    /// <summary>Leading git-generated comment lines must not be mistaken for the header.</summary>
    [Fact]
    public void CommentLinesAreNotTreatedAsTheHeader()
    {
        var (exitCode, output) = RunHookWithRawMessage(
            "# a comment git put here\nchore: a short header\n");

        Assert.True(
            exitCode == 0,
            $"The hook measured a comment line instead of the header. Output: {output}");
    }

    private static (int ExitCode, string Output) RunHook(string header) =>
        RunHookWithRawMessage(header + "\n\nA body line.\n");

    private static (int ExitCode, string Output) RunHookWithRawMessage(string message)
    {
        var root = TestProject.FindRepositoryRoot();
        var hook = Path.Combine(root, ".githooks", "commit-msg");

        Assert.True(
            File.Exists(hook),
            $"'{hook}' does not exist. The hook is tracked in the repository so that a clone can " +
            "enable it with `git config core.hooksPath .githooks`.");

        var messageFile = Path.Combine(Path.GetTempPath(), $"ragnet-commit-msg-{Guid.NewGuid():N}");
        File.WriteAllText(messageFile, message, new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo(FindShell())
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(hook);
            psi.ArgumentList.Add(messageFile);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(messageFile);
        }
    }

    /// <summary>Locates a POSIX shell to run the hook with.</summary>
    /// <remarks>
    /// Both CI legs have one: ubuntu at /bin/bash, and the Windows runner ships Git Bash. This
    /// throws rather than skipping, because a guard that quietly stops running is the shape this
    /// phase exists to remove.
    /// </remarks>
    private static string FindShell()
    {
        string[] candidates =
        [
            "/bin/bash",
            "/usr/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No bash found to execute .githooks/commit-msg. Looked in: " +
            string.Join(", ", candidates) +
            ". Both CI legs provide one, so this means the test environment is not what it was " +
            "written against.");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`

Expected: **FAIL**, four tests, each reporting that `.githooks/commit-msg` does not exist.

- [ ] **Step 3: Write the hook**

Create `.githooks/commit-msg` with exactly this content:

```bash
#!/usr/bin/env bash
# Refuse a commit header that commitlint will reject in CI.
#
# SCOPE IS ONE RULE: the 100-character header cap. This is deliberately NOT a local
# reimplementation of commitlint. .commitlintrc.yml tunes type-enum with `bench`, sets
# subject-case to 0, and sets body-max-line-length to 0 because bodies quote URLs and error
# messages verbatim. A second implementation of those rules would drift from the first, and the
# drift would surface as a false rejection. CI stays authoritative; see the commitlint job.
#
# Why a hook at all: commitlint runs in CI only, over the commits a pull request adds. The loop
# for a long header is commit, push, open a PR, wait, rewrite history, force-push, and when the
# offending commit is not the tip, `git commit --amend` cannot reach it. Phase 6.2.42.
#
# ENABLE PER CLONE:  git config core.hooksPath .githooks
# A tracked hook does nothing until that line is run. That is a known and accepted cost.
set -euo pipefail

cap=100

# The header is the first line that is not a git-generated comment.
header=$(sed -n '/^[^#]/{p;q;}' "$1")

# ${#header} in bash counts CHARACTERS in a UTF-8 locale, not bytes. This matters: em dashes are
# one character and three bytes, and this repository's headers use them, so a byte count would
# reject valid headers while looking like a working guard. Run under bash, never sh: dash counts
# bytes.
length=${#header}

if [ "$length" -gt "$cap" ]; then
  {
    printf 'commit-msg: the header is %d characters; the cap is %d.\n\n' "$length" "$cap"
    printf '  %s\n\n' "$header"
    printf 'commitlint enforces this in CI over every commit a pull request adds, so a header\n'
    printf 'this long fails the build after the push, and rewriting a commit that is not the tip\n'
    printf 'costs a rebase rather than an amend.\n\n'
    printf 'Shorten the header and move the detail into the body. Body line length is not capped\n'
    printf 'here: .commitlintrc.yml sets body-max-line-length to 0 on purpose.\n'
  } >&2
  exit 1
fi
```

Mark it executable so the mode is tracked in git:

```bash
git update-index --add --chmod=+x .githooks/commit-msg
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Rag.NET.RepoConventions.Tests -c Release`

Expected: **PASS**, all four. If `AHeaderAtTheCapContainingEmDashesIsAccepted` fails, the script is
counting bytes — confirm the shebang is `bash` and not `sh`.

- [ ] **Step 5: Verify the hook end to end against real git**

The test invokes the script directly; this confirms git actually calls it.

```bash
git config core.hooksPath .githooks
long="chore: $(printf 'x%.0s' $(seq 1 100))"
git commit --allow-empty -m "$long" ; echo "exit=$?"     # expected: exit=1, with the message
git commit --allow-empty -m "chore: a short header"      # expected: succeeds
git reset --hard HEAD~1                                  # drop the empty commit
```

Leave `core.hooksPath` configured — that is the whole point.

- [ ] **Step 6: Document the one-time setup line**

In `docs/reference/ci.md`, add this section beside the existing local-workflow guidance:

````markdown
### Catching a long commit header before you push

`commitlint` runs in CI only, and it lints **every commit a pull request adds** — not just the tip.
A header over 100 characters therefore fails after the push, and if the offending commit is not the
tip, fixing it costs a rebase rather than an amend.

A tracked hook catches it at `git commit` instead. Enable it once per clone:

```bash
git config core.hooksPath .githooks
```

It checks **header length only**. It is not a local reimplementation of commitlint — this repository
tunes `type-enum`, `subject-case` and `body-max-line-length` in `.commitlintrc.yml`, and a second
implementation of those rules would drift from the first. CI remains authoritative.

**The hook does nothing until you run that line.** It is tracked, not installed.
````

- [ ] **Step 7: Commit**

```bash
git add .githooks/commit-msg \
        tests/Rag.NET.RepoConventions.Tests/CommitMessageHookTests.cs \
        docs/reference/ci.md
git commit -m "build: add a tracked commit-msg hook that refuses a header over the cap"
```

---

### Task 3: Guard B — make the BEIR skip say the corpus is here

**Files:**
- Modify: `src/Rag.NET.Benchmarks.Quality/BeirDatasetCache.cs` — add one static method
- Modify: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs` — `SkipReason`
- Create: `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/SkipMessageTests.cs`
- Modify: `tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs`
- Modify: `tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs`

**Interfaces:**
- Consumes: `BeirDatasetCache.ResolveCacheDirectoryFromEnvironment()`, existing, returns the value of
  `RAGNET_BEIR_CACHE` or `null` when unset or blank. **Its behaviour does not change.**
- Produces: `public static string? BeirDatasetCache.DescribeUnreferencedConventionalCache()` —
  a sentence when the conventional directory exists and the environment does not point at it, and
  `null` otherwise.

**What is and is not changing.** `IsProvisioned` and `IsDatasetCacheProvisioned` keep their exact
current conditions, so **the same tests skip in the same situations**. Only the message changes, and
only on the branch where the data is present but unreferenced. `Rag.NET.Benchmarks.Quality` is
`IsPackable=false`, so nothing ships.

**Why the Onnx tests are included even though they read a different variable.** They gate on
`RAGNET_ONNX_EMBED_VOCAB`, not `RAGNET_BEIR_CACHE` — but both are set by the same
`~/.cache/ragnet-beir/env.sh`, and the measured "10 skips to 0" came from that project. A private
helper is duplicated there rather than shared, because `Rag.NET.Embeddings.Onnx.Tests` references
neither `Rag.NET.Benchmarks.Quality` nor `Rag.NET.Testing`, and one sentence does not justify coupling
two unrelated test projects.

- [ ] **Step 1: Write the failing test**

Create `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/SkipMessageTests.cs`:

```csharp
using Rag.NET.Benchmarks.Quality;
using Xunit;

namespace Rag.NET.Benchmarks.Quality.IntegrationTests;

/// <summary>
/// Pins what an unprovisioned skip message tells the reader.
/// </summary>
/// <remarks>
/// Three separate sessions recorded this corpus as "unprovisioned" while it sat at
/// <c>~/.cache/ragnet-beir</c> with an <c>env.sh</c> beside it — the third time after a note in
/// STATE.md saying two sessions had already done it. The skip was correct every time; the message
/// was a dead end. This pins the difference between "not here" and "here and unreferenced".
/// </remarks>
public sealed class SkipMessageTests
{
    /// <summary>With the variable set, there is nothing to hint about.</summary>
    [Fact]
    public void NoHintWhenTheEnvironmentAlreadyPointsAtACache()
    {
        var previous = Environment.GetEnvironmentVariable(BeirDatasetCache.CacheDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                BeirDatasetCache.CacheDirectoryVariable, Path.GetTempPath());

            Assert.Null(BeirDatasetCache.DescribeUnreferencedConventionalCache());
        }
        finally
        {
            Environment.SetEnvironmentVariable(BeirDatasetCache.CacheDirectoryVariable, previous);
        }
    }

    /// <summary>The hint names the directory and the file to source.</summary>
    /// <remarks>
    /// Runs only where the conventional directory exists, because the hint is about a real
    /// directory. On a machine without one there is nothing to assert.
    /// </remarks>
    [Fact]
    public void TheHintNamesTheDirectoryWhenTheCacheIsPresentButUnreferenced()
    {
        var conventional = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ragnet-beir");

        Assert.SkipUnless(
            Directory.Exists(conventional),
            $"No conventional cache at '{conventional}' on this machine, so there is no " +
            "present-but-unreferenced case to assert.");

        var previous = Environment.GetEnvironmentVariable(BeirDatasetCache.CacheDirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(BeirDatasetCache.CacheDirectoryVariable, null);

            var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache();

            Assert.NotNull(hint);
            Assert.Contains("ragnet-beir", hint, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BeirDatasetCache.CacheDirectoryVariable, previous);
        }
    }

    /// <summary>The skip reason carries the hint exactly when there is one.</summary>
    [Fact]
    public void TheSkipReasonCarriesTheHint()
    {
        var hint = BeirDatasetCache.DescribeUnreferencedConventionalCache();

        if (hint is null)
        {
            Assert.DoesNotContain("ragnet-beir", BeirHarness.SkipReason, StringComparison.Ordinal);
            return;
        }

        Assert.Contains(hint, BeirHarness.SkipReason, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release`

Expected: **build failure** — `DescribeUnreferencedConventionalCache` does not exist. That is the
failing state for this task.

- [ ] **Step 3: Add the helper**

In `src/Rag.NET.Benchmarks.Quality/BeirDatasetCache.cs`, directly beneath
`ResolveCacheDirectoryFromEnvironment`, add:

```csharp
    /// <summary>
    /// Describes a conventional cache that exists but is not referenced by the environment.
    /// </summary>
    /// <returns>
    /// A sentence naming the directory and its <c>env.sh</c>, or <see langword="null"/> when the
    /// environment already points somewhere or no conventional cache is present.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>This does not change what is resolved.</b> It reports; it never becomes a fallback.
    /// Silently adopting a directory the caller did not name would turn a visible skip into an
    /// invisible hour of re-embedding against a corpus nobody asked for.
    /// </para>
    /// <para>
    /// It exists because "unprovisioned" is a dead end that three separate sessions walked into
    /// while the corpus sat at the conventional path with its <c>env.sh</c> beside it.
    /// </para>
    /// </remarks>
    public static string? DescribeUnreferencedConventionalCache()
    {
        if (ResolveCacheDirectoryFromEnvironment() is not null)
        {
            return null;
        }

        var conventional = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ragnet-beir");

        if (!Directory.Exists(conventional))
        {
            return null;
        }

        var envScript = Path.Combine(conventional, "env.sh");

        return File.Exists(envScript)
            ? $" A cache is already present at '{conventional}' and nothing points at it: source " +
              $"'{envScript}' to use it."
            : $" A cache directory is already present at '{conventional}' and nothing points at " +
              $"it: set {CacheDirectoryVariable} to it to use it.";
    }
```

Confirm `CacheDirectoryVariable` is `public`. If it is `private` or `internal`, widen it — the test
and the message both name it, and a constant naming an environment variable is not an implementation
detail.

- [ ] **Step 4: Make the skip reasons carry the hint**

In `tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs`, change `SkipReason` from a
`const` to a computed property. All 30 call sites pass it as a method argument to `Assert.SkipUnless`
/ `Assert.SkipWhen`; **none uses it in an attribute**, so this is source-compatible — verified before
this plan was written.

```csharp
    /// <summary>The message an unprovisioned run skips with.</summary>
    /// <remarks>
    /// Computed rather than constant so it can name a corpus that is present but unreferenced; see
    /// <see cref="BeirDatasetCache.DescribeUnreferencedConventionalCache"/>. Three sessions recorded
    /// this machine as unprovisioned while the corpus sat at the conventional path.
    /// </remarks>
    public static string SkipReason =>
        "Set RAGNET_ONNX_EMBED_MODEL and RAGNET_ONNX_EMBED_VOCAB to an existing all-MiniLM-L6-v2 " +
        "ONNX export (token-level output) and its WordPiece vocab.txt, and RAGNET_BEIR_CACHE to a " +
        "writable directory for the dataset downloads, to run the BEIR measurements." +
        BeirDatasetCache.DescribeUnreferencedConventionalCache();
```

`DescribeUnreferencedConventionalCache()` returns `null` in the common case, and `string + null`
appends nothing — so the message is byte-identical to today's when there is no hint.

In **both** `tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs` and
`tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs`, add this private helper as a member
of the class in **each** file:

```csharp
    /// <summary>Names a conventional cache whose env.sh exists, or nothing.</summary>
    /// <remarks>
    /// Duplicated rather than shared: this project references neither Rag.NET.Benchmarks.Quality nor
    /// Rag.NET.Testing, and one sentence does not justify coupling two unrelated test projects.
    /// These tests gate on RAGNET_ONNX_EMBED_VOCAB rather than the BEIR cache, but the same env.sh
    /// sets both — sourcing it took this project from 10 skips to 0.
    /// </remarks>
    private static string ConventionalCacheHint()
    {
        var envScript = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "ragnet-beir",
            "env.sh");

        return File.Exists(envScript)
            ? $" '{envScript}' exists on this machine and sets these variables: source it."
            : string.Empty;
    }
```

Then change each file's `private const string SkipReason` to a property, preserving that file's own
wording. In `WhitespaceNormalizationTests.cs`:

```csharp
    private static string SkipReason =>
        "Set RAGNET_ONNX_EMBED_VOCAB to an existing WordPiece vocab.txt (e.g. all-MiniLM-L6-v2's) " +
        "to run the whitespace normalization pins." + ConventionalCacheHint();
```

Apply the same shape in `NormalizationGuardTests.cs`, keeping its existing message text unchanged.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release
dotnet test tests/Rag.NET.Embeddings.Onnx.Tests -c Release
```

Expected: both **PASS**. The benchmark project gains 3 tests. Record both projects' triples.

- [ ] **Step 6: Prove no skip condition moved**

This is the constraint the design places on Guard B, and it must be checked rather than asserted.

```bash
git diff -- src/ tests/ | grep -E "^[+-]" | grep -vE "^(\+\+\+|---)" | \
  grep -viE "SkipReason|ConventionalCacheHint|DescribeUnreferencedConventionalCache|///|^[+-]\s*$"
```

Expected: **nothing mentioning `IsProvisioned`, `IsDatasetCacheProvisioned`, `Assert.Skip`, or any
gate condition.** Anything else that appears is out of scope and must be reverted.

- [ ] **Step 7: Commit**

```bash
git add src/Rag.NET.Benchmarks.Quality/BeirDatasetCache.cs \
        tests/Rag.NET.Benchmarks.Quality.IntegrationTests/BeirHarness.cs \
        tests/Rag.NET.Benchmarks.Quality.IntegrationTests/SkipMessageTests.cs \
        tests/Rag.NET.Embeddings.Onnx.Tests/WhitespaceNormalizationTests.cs \
        tests/Rag.NET.Embeddings.Onnx.Tests/NormalizationGuardTests.cs
git commit -m "test(beir): say the corpus is here when the skip is only a missing variable"
```

---

### Task 4: Verify the phase and record it

**Files:**
- Modify: `docs/planning/ROADMAP.md` — phase status to complete
- Modify: `docs/planning/MILESTONE.md` — the 6.2.42 row
- Modify: `docs/planning/STATE.md` — the session entry
- Create: `docs/pre-push-review-YYYY-MM-DD-HHmm.md` via the pre-push-review skill

**Interfaces:** consumes the three guards; produces the phase record.

- [ ] **Step 1: Run every affected suite, as literal commands**

Enumerated, not reasoned about — the rule this phase is named for. Run each and record its triple:

```bash
dotnet test tests/Rag.NET.RepoConventions.Tests -c Release
dotnet test tests/Rag.NET.PackageValidation.Tests -c Release
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release
dotnet test tests/Rag.NET.Embeddings.Onnx.Tests -c Release
dotnet test tests/Rag.NET.Tests -c Release
dotnet build Rag.NET.slnx -c Release
npm run build
```

Baselines: `RepoConventions` 101 passed / 2 skipped **before** this phase's six new tests;
`PackageValidation` 23; `Rag.NET.Tests` 1499; build **0 warnings**.

**`PackageValidation` reads `artifacts/packages/`.** If it fails on
`EveryPackageCarriesTheVersionGitVersionDerives`, those packages are stale from another branch —
repack rather than editing anything:

```bash
rm -rf artifacts/packages
dotnet tool restore
version=$(dotnet dotnet-gitversion "$(pwd -W)" /output json | jq -r '.SemVer')
dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version="$version"
```

On Git Bash, `dotnet-gitversion` reports `Cannot find the .git directory` when given a POSIX path;
`pwd -W` yields the Windows-style path it accepts.

- [ ] **Step 2: Source `env.sh` and re-run the two BEIR-gated projects**

**Do this before writing the word "unprovisioned" anywhere.** Three sessions have now recorded this
machine as unprovisioned while the corpus was present.

```bash
source ~/.cache/ragnet-beir/env.sh
dotnet test tests/Rag.NET.Embeddings.Onnx.Tests -c Release
dotnet test tests/Rag.NET.Benchmarks.Quality.IntegrationTests -c Release
```

Expected provisioned: `Embeddings.Onnx.Tests` **0 skips**; the benchmark project around
**175 passed / 92 skipped**. Record both the unprovisioned and provisioned triples — that difference
is the evidence Guard B's message exists to convey.

- [ ] **Step 3: Confirm every commit header is within the cap**

The hook only helps if it was enabled; this checks the branch regardless.

```bash
git log origin/main..HEAD --format="%s" | awk '{ print length($0)"\t"$0 }'
```

Expected: every length `<= 100`. Anything longer must be fixed before pushing, because CI lints every
commit a PR adds, and a non-tip commit needs a rebase rather than an amend.

- [ ] **Step 4: Run the pre-push review**

Invoke the `pre-push-review` skill. The report must record:

- The **deliberate-failure output from Task 1 Step 5, verbatim**. This is the phase's central claim
  and the one thing a green run does not demonstrate.
- Both BEIR triples from Step 2, unprovisioned and provisioned.
- **That adoption of Guard A is untested and untestable** — a hook does nothing until someone runs
  `git config core.hooksPath .githooks`. State it plainly rather than implying the rule is now
  enforced for everyone.
- Whether `Rag.NET.E2ETests` ran. It is `RequiresLlm`, nightly-only, and not covered here.

- [ ] **Step 5: Update the planning records and open the PR**

Set the ROADMAP phase status to complete, update the MILESTONE row, and add a STATE.md entry at the
merge. Then open the PR. **Do not merge it** — the operator merges.

- [ ] **Step 6: Commit**

```bash
git add docs/planning/ROADMAP.md docs/planning/MILESTONE.md docs/planning/STATE.md \
        docs/pre-push-review-2026-09-12-HHmm.md
git commit -m "chore(roadmap): complete phase 6.2.42 — three guards that fire by themselves"
```

**Stage the review by its explicit filename, never `git add docs/`** — that directory holds older
untracked review files that must not be swept into this commit.

---

## Self-Review

**1. Spec coverage.** Design §1 Guard A → Task 2. §2 Guard B → Task 3. §3 Guard C → Task 1. §4 the
third rule stays prose → no task, correctly. §5 Scope items 1-5 → Tasks 2, 2, 3, 2, 1. §5 "Out" items
are each honoured: no commitlint reimplementation, no skip-condition change, no auto-install, no
touching the #571 race, no wider reporting work. §6 Verifiability: Guard A → Task 2 Steps 4-5, Guard B
→ Task 3 Steps 5-6, Guard C → Task 1 Step 5, adoption caveat → Task 4 Step 4.

**2. Placeholder scan.** No TBD, no "handle edge cases", no "similar to Task N". Every code step
carries its literal content.

**3. Type consistency.** `DescribeUnreferencedConventionalCache()` is defined in Task 3 Step 3 and
used under that exact name in Steps 1 and 4. `ConventionalCacheHint()` is private and duplicated by
design, stated where introduced. `TestProject.FindRepositoryRoot()` matches the existing helper.
`BeirDatasetCache.CacheDirectoryVariable` carries an explicit accessibility check.

**A risk that was open at scoping and is now closed.** At scoping, the log encoding was verified on
Windows only, and this self-review flagged the Linux encoding as unverified. Task 1 closed it by
evidence: both the implementer and an independent re-reviewer built the same throwaway
deliberately-failing project inside a fresh `mcr.microsoft.com/dotnet/sdk:10.0` container, with no
reused Windows build output, and read the same `FF FE` UTF-16LE BOM there. **Linux and Windows produce
the identical encoding.** The `iconv` branch is the one that fires on every platform checked; the
`else: cat` branch sniffed for is dead code on the evidence gathered so far, kept only because a future
runner could still disagree.

**A known limitation of Guard A, not a defect.** The hook measures every commit message, including
merge commits git generates itself. A local merge of a long branch name could be refused. This was
left unhandled rather than special-cased, because the design scopes Guard A to header length only and
an untested exemption branch is worth less than the simplicity it costs.
