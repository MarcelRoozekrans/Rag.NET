using System.Text;
using Xunit;

namespace Rag.NET.PackageValidation.Tests;

/// <summary>
/// Pins ci.yml's pack-validate wiring, unconditionally — this is what stops the skip gate in
/// <see cref="ProducedPackageTests"/> from rotting into permanent green.
/// </summary>
/// <remarks>
/// The package checks skip when <c>artifacts/packages</c> does not exist, which is correct on the
/// matrix legs and on a fresh checkout — but a skip gate whose provisioning step someone deletes
/// or renames skips everywhere, forever, while looking like coverage. This test runs in the fast
/// tier on every push, so the moment ci.yml stops packing into the directory the package checks
/// read, or stops running this project after packing, a gating job goes red. Asserted against the
/// workflow's commands with comment lines stripped, the same way and for the same measured reason
/// as <c>TestProjectTierTests</c>: prose must not satisfy an assertion about what runs.
/// </remarks>
public sealed class WorkflowWiringTests
{
    [Fact]
    public void TheWorkflowPacksAndThenValidatesWhatItProduced()
    {
        var workflow = Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml");

        Assert.True(
            File.Exists(workflow),
            $"'{workflow}' does not exist. The package validation in this project only ever " +
            "runs if a workflow packs first; without ci.yml nothing does.");

        var commands = ReadWorkflowCommands(workflow);

        // Twice, not once: pack-validate packs what it validates, and publish-nuget packs what
        // it pushes with a character-identical command — the whole point being that the release
        // packs with a command that has run green on every push. The -p:Version is part of the
        // pinned text because dropping it is silent: pack falls back to the SDK default 1.0.0
        // and exits 0.
        Assert.Equal(
            2,
            CountOf(commands, PinnedPackCommand));

        Assert.Contains(
            "dotnet test tests/Rag.NET.PackageValidation.Tests -c Release",
            commands,
            StringComparison.Ordinal);

        // Order is part of the wiring, and Contains is order-blind — moving the validate step
        // above Pack left every assertion here green (proven by mutation, 2026-08-03) while in
        // CI the validation would run before artifacts/packages exists: every package check
        // skips, and the job passes having validated nothing.
        Assert.True(
            commands.IndexOf(PinnedPackCommand, StringComparison.Ordinal)
                < commands.IndexOf(
                    "dotnet test tests/Rag.NET.PackageValidation.Tests",
                    StringComparison.Ordinal),
            "ci.yml runs the package validation before the pack command. The validation reads " +
            "artifacts/packages, which only the pack step creates — in this order every " +
            "package check skips on a missing directory and the job is green having " +
            "validated nothing.");
    }

    [Fact]
    public void ThePackValidateJobRunsUnconditionally()
    {
        // publish-nuget's `if:` is pinned verbatim below because that job must be gated;
        // pack-validate must have no `if:` at all, and only this test says so. An `if: false`
        // under the job key left every other pin in this class green (proven by mutation,
        // 2026-08-03) — the commands are still in the file — while CI never packs, never
        // validates, never rehearses the push, and ProducedPackageTests skips everywhere
        // because artifacts/packages is never created: the exact permanent green this class
        // exists to prevent.
        var commands = ReadWorkflowCommands(Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml"));

        var jobStart = commands.IndexOf(
            "pack-validate: runs-on: ubuntu-latest",
            StringComparison.Ordinal);

        Assert.True(
            jobStart >= 0,
            "'pack-validate:' is not immediately followed by 'runs-on: ubuntu-latest' in " +
            "ci.yml. Either the job is gone, or something — an `if:` gate, say — now sits " +
            "between the job key and runs-on. A conditional pack-validate is a skip gate " +
            "nothing pins: every command this class asserts on stays in the file while none " +
            "of it ever runs.");

        var jobEnd = commands.IndexOf("publish-nuget:", jobStart, StringComparison.Ordinal);

        Assert.True(
            jobEnd > jobStart,
            "The publish-nuget job no longer follows pack-validate in ci.yml, so this test " +
            "cannot delimit the pack-validate job to prove it carries no condition.");

        // Bash conditionals in the job's scripts are `if [`/`if !`, never `if:` — so this
        // catches a YAML condition anywhere in the job, at the job level or on a step.
        Assert.DoesNotContain("if:", commands[jobStart..jobEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflowDerivesTheVersionThePackagesPack()
    {
        // A GitVersion.yml no build step consumes is decoration. This pins the consumption:
        // both packing jobs restore the pinned tool, derive the version, and hand it to the
        // pinned pack command — so the moment someone deletes the derive step or the -p:Version
        // flag, a gating job goes red instead of every package silently reverting to the SDK
        // default 1.0.0. EveryPackageCarriesTheVersionGitVersionDerives closes the loop from
        // the other side, by re-deriving after the pack and reading the produced packages.
        var commands = ReadWorkflowCommands(Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml"));

        // The derive command, in both packing jobs: the local tool (pinned by
        // .config/dotnet-tools.json), JSON output, jq — the house convention.
        Assert.Equal(
            2,
            CountOf(commands, "version=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')"));

        // The guard that makes an empty derivation loud. `-p:Version=` with an empty value
        // packs 1.0.0 without a word, which is the exact silence the derive step exists to end.
        Assert.Equal(
            2,
            CountOf(commands, "if ! [[ \"$version\" =~ ^[0-9]+\\.[0-9]+\\.[0-9]+ ]]; then"));
    }

    [Fact]
    public void TheCommitConventionIsEnforcedOnEveryPullRequest()
    {
        // .commitlintrc.yml with no workflow running it is decoration — the same shape as a
        // GitVersion.yml nothing consumes. release-please derives release versions from commit
        // messages, so the linting is load-bearing, and it must run where commits are
        // introduced: on the pull request, over exactly the commits the PR adds. History stays
        // unlinted deliberately (70 of 1,506 commits fail, none after 2026-07-29 — measured
        // 2026-08-03); the start point is the commit that introduced the config.
        var root = ProducedPackageTests.FindRepositoryRoot();

        Assert.True(
            File.Exists(Path.Combine(root, ".commitlintrc.yml")),
            "'.commitlintrc.yml' does not exist at the repository root, so the commitlint job " +
            "lints against nothing but defaults — including rules this repository measured and " +
            "deliberately turned off.");

        var commands = ReadWorkflowCommands(Path.Combine(root, ".github", "workflows", "ci.yml"));

        Assert.Contains(
            "commitlint --from ${{ github.event.pull_request.base.sha }} --to ${{ github.event.pull_request.head.sha }}",
            commands,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseWorkflowIsGatedAndItsGateIsWrittenDown()
    {
        // release-please is the one genuinely unexercised path Phase 4.1 ships: its only
        // observable effects — a release PR, a tag, a GitHub release — ARE the release, so
        // unlike the push there is no local-feed rehearsal to run on every push. What this
        // repository's history demands for such a path (nightly.yml failing on its first-ever
        // run; the OCR test that is not skipped but not compiled) is that the gate be named,
        // its condition stated, its procedure documented — and pinned, so it cannot drift or
        // be deleted while looking wired.
        var root = ProducedPackageTests.FindRepositoryRoot();
        var workflow = Path.Combine(root, ".github", "workflows", "release-please.yml");

        Assert.True(
            File.Exists(workflow),
            $"'{workflow}' does not exist. Without it nothing creates the release PR or the " +
            "vX.Y.Z tag that flips GitVersion from prerelease to stable, and Phase 6.3's " +
            "documented procedure dispatches a workflow that is not there.");

        var commands = ReadWorkflowCommands(workflow);

        // The condition: manual dispatch only, on main. A push trigger would open a release PR
        // on every merge, months before 6.3 decides the version.
        Assert.Contains("on: workflow_dispatch:", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", commands, StringComparison.Ordinal);
        Assert.Contains(
            "if: github.ref == 'refs/heads/main'",
            commands,
            StringComparison.Ordinal);

        Assert.Contains(
            "uses: googleapis/release-please-action",
            commands,
            StringComparison.Ordinal);

        // The documented procedure that satisfies the gate — fenced, because a runnable
        // command is a procedure and a sentence mentioning one is not.
        var documented = ReadFencedCommands(Path.Combine(root, "docs", "reference", "ci.md"));

        Assert.Contains(
            "gh workflow run release-please.yml --ref main",
            documented,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflowRehearsesThePushAgainstALocalFeed()
    {
        // The push to nuget.org cannot run before Phase 6.3, which makes it exactly the inert
        // path this repository keeps finding defects in — so ci.yml pushes every produced
        // package to a local directory feed on every run, twice, and asserts arrival. This test
        // is what stops that rehearsal from being deleted while the real push stays gated: lose
        // the rehearsal and the first execution of `dotnet nuget push` is the release itself.
        var commands = ReadWorkflowCommands(Path.Combine(
            ProducedPackageTests.FindRepositoryRoot(), ".github", "workflows", "ci.yml"));

        // The exact command 6.3 runs, minus credential and endpoint: the quoted glob NuGet
        // expands itself, and --skip-duplicate, the deliberate duplicate policy — nuget.org
        // never forgets a version, so a partial push must be re-runnable without failing on
        // what already arrived. Twice, not once: the arrival rehearsal and the duplicate-push
        // rehearsal run the identical command, and Contains was satisfied by either alone —
        // emptying the first push left this pin green on the second (proven by mutation,
        // 2026-08-03).
        Assert.Equal(
            2,
            CountOf(
                commands,
                "dotnet nuget push \"artifacts/packages/*.nupkg\" --source \"$feed\" --skip-duplicate"));

        // The .snupkg push is a measured silent no-op against a directory feed (exit 0, nothing
        // delivered, 2026-08-03); the workflow attempts it and asserts non-arrival so the day
        // NuGet changes that, the run fails and the rehearsal widens. This pin keeps the attempt
        // itself from being removed as "does nothing anyway" — doing nothing loudly is its job.
        Assert.Contains(
            "dotnet nuget push \"artifacts/packages/*.snupkg\" --source \"$feed\" --skip-duplicate",
            commands,
            StringComparison.Ordinal);

        // The rehearsal's teeth, pinned as the check commands rather than their error prose:
        // a push that no-ops silently exits 0, so only reading the feed back proves delivery.
        // The per-file arrival test, the .snupkg count, and the count's comparison — the error
        // messages stay unpinned deliberately, because the checks are what run.
        Assert.Contains(
            "if [ ! -f \"$feed/$(basename \"$package\")\" ]; then",
            commands,
            StringComparison.Ordinal);
        Assert.Contains(
            "snupkg_arrived=$(find \"$feed\" -name '*.snupkg' -type f | wc -l)",
            commands,
            StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$snupkg_arrived\" -ne 0 ]; then",
            commands,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheNugetOrgPushIsGatedAndItsGateIsWrittenDown()
    {
        // TestGateTests holds every test gate in this repository to: named, condition stated,
        // satisfiable by a documented procedure. It scans test gates only — RAGNET_* variables,
        // #if symbols, skip attributes — so the publish-nuget workflow gate sits outside it, and
        // this test is the pin that holds it to the same standard instead. Extending
        // TestGateTests' scanner to workflows was considered and declined: one gate does not
        // justify a general workflow-gate scanner, and this pin fails a gating push the moment
        // the gate is renamed, its condition changes, its endpoint drifts, or its documented
        // procedure is deleted.
        var root = ProducedPackageTests.FindRepositoryRoot();
        var commands = ReadWorkflowCommands(Path.Combine(root, ".github", "workflows", "ci.yml"));

        // The condition: manual dispatch, the explicit input, and main. Anything weaker and the
        // push stops being gated; anything the repository cannot satisfy and it stops being a
        // gate — "satisfiable nowhere" is exactly what TestGateTests fails other gates on.
        Assert.Contains(
            "if: github.event_name == 'workflow_dispatch' && inputs.publish_to_nuget && github.ref == 'refs/heads/main'",
            commands,
            StringComparison.Ordinal);

        // The real push: same glob and same duplicate policy the local-feed rehearsal executes
        // on every run, plus the two things nothing can exercise before 6.3 — the endpoint and
        // the credential. Character-identical to the stored-key version it replaced: moving to
        // Trusted Publishing changed where $NUGET_API_KEY comes from, not the command, so the
        // command that has run green against a local feed on every push is still the one that
        // runs on release day.
        Assert.Contains(
            "dotnet nuget push \"artifacts/packages/*.nupkg\" --source https://api.nuget.org/v3/index.json --api-key \"$NUGET_API_KEY\" --skip-duplicate",
            commands,
            StringComparison.Ordinal);

        // Trusted Publishing's three moving parts, pinned because the push command deliberately
        // does not move when they do — which is what makes the command stable and also what
        // would let the credential silently revert to a stored secret with every assertion above
        // still green.
        Assert.Contains("uses: NuGet/login@v1", commands, StringComparison.Ordinal);
        Assert.Contains(
            "NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}",
            commands,
            StringComparison.Ordinal);

        // id-token: write is what lets the OIDC token be minted at all, and a job-level
        // permissions block replaces the workflow-level one, so contents: read has to be
        // restated beside it or the checkout loses read access.
        Assert.Contains("permissions: contents: read id-token: write", commands, StringComparison.Ordinal);

        // The documented procedure that satisfies the gate, held to TestGateTests' own standard
        // for procedures: a fenced command in docs/reference/ci.md, because a runnable command
        // is a procedure and a sentence mentioning one is not.
        var documented = ReadFencedCommands(Path.Combine(root, "docs", "reference", "ci.md"));

        Assert.Contains("gh variable set NUGET_USER", documented, StringComparison.Ordinal);
        Assert.Contains(
            "gh workflow run ci.yml --ref main -f publish_to_nuget=true",
            documented,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The pack command both packing jobs run, character-identically: pack-validate packs what
    /// it validates and publish-nuget packs what it pushes, so the release day command is the
    /// one that has run green on every push. <c>-p:Version</c> is part of the pin because its
    /// absence is silent — pack falls back to the SDK default 1.0.0 and exits 0.
    /// </summary>
    private const string PinnedPackCommand =
        "dotnet pack Rag.NET.slnx -c Release -o artifacts/packages -p:Version=\"$PACKAGE_VERSION\"";

    /// <summary>
    /// Counts non-overlapping occurrences of <paramref name="needle"/>, because some pins must
    /// hold in two jobs at once and <c>Assert.Contains</c> is satisfied by one.
    /// </summary>
    /// <param name="haystack">The normalized workflow text.</param>
    /// <param name="needle">The exact command text to count.</param>
    /// <returns>The number of occurrences.</returns>
    private static int CountOf(string haystack, string needle)
    {
        var count = 0;

        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Reads the fenced command blocks of a documentation page, the same way and for the same
    /// reason as <c>TestGateTests</c>: only a runnable command counts as a procedure, so only
    /// fenced lines are read and prose cannot satisfy the assertion.
    /// </summary>
    /// <param name="path">The absolute path of the markdown page.</param>
    /// <returns>The fenced lines joined into one string.</returns>
    private static string ReadFencedCommands(string path)
    {
        Assert.True(
            File.Exists(path),
            $"'{path}' does not exist, so the gate's documented procedure has nowhere to live.");

        var commands = new StringBuilder();
        var inFence = false;

        foreach (var line in File.ReadLines(path))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                _ = commands.Append(line).Append(' ');
            }
        }

        return commands.ToString();
    }

    /// <summary>
    /// Reads a workflow file as the commands it will run: comment lines removed, shell line
    /// continuations joined, runs of whitespace collapsed to one space. Duplicated from
    /// <c>Rag.NET.RepoConventions.Tests.TestProject</c> deliberately — this project references
    /// nothing in the repository; see the csproj comment.
    /// </summary>
    /// <param name="workflowPath">The absolute path of the workflow file.</param>
    /// <returns>The workflow's non-comment text on a single line.</returns>
    private static string ReadWorkflowCommands(string workflowPath)
    {
        var builder = new StringBuilder();

        foreach (var line in File.ReadLines(workflowPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed[^1] == '\\')
            {
                trimmed = trimmed[..^1];
            }

            _ = builder.Append(trimmed).Append(' ');
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
