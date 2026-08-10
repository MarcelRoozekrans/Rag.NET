using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that no test is gated behind a condition nothing satisfies: every gate must be
/// satisfiable somewhere, and where it is satisfiable must be written down.
/// </summary>
/// <remarks>
/// <para>
/// A gated test that can never run reports green by skipping — it looks like coverage and is not.
/// This milestone found four such gates before this guard existed: three env-gated tests whose
/// secrets no workflow ever set, and one behind <c>#if ENABLE_OCR</c>, which no build defines — so
/// that one is not even skipped, it is <b>not compiled</b>, and appears in no test count at all.
/// </para>
/// <para>
/// Three gate shapes, three checks. <b>Environment-variable gates</b> (the <c>Assert.SkipWhen</c> /
/// <c>Assert.SkipUnless</c> sites): every <c>RAGNET_</c> variable a test names must be written to
/// by a provisioning command in <c>ci.yml</c> or <c>nightly.yml</c> (<c>NAME=…</c> in the commands,
/// comments stripped), or set by a fenced command in <c>docs/reference/ci.md</c>. A
/// <c>secrets.*</c> mapping alone does not count: the repository cannot show the secret exists, and
/// the nightly presence reports measured the unprovisioned ones unset on every run to date.
/// <b>Unconditional <c>[Fact(Skip=…)]</c></b>: legitimate when the environment genuinely cannot
/// exist, but each must be recorded here with its reason — visible, like the
/// <c>FeatureClaimTests.KnownFalseClaims</c> ledger, never silently accepted.
/// <b>Conditional-compilation symbols</b>: scanned from raw source, deliberately — a check over
/// compiled output is blind to exactly the worst case, because the gated test is not in the
/// assembly. Each custom symbol must be defined by some build file whose condition something
/// satisfies, or by a workflow or documented command.
/// </para>
/// <para>
/// Gates that cannot currently be satisfied are recorded in the ledgers below, each with its
/// reason and owning work, and a staleness guard fails the moment an entry stops matching
/// reality — so an exemption cannot outlive its defect. Recording is Phase 4.0's deliverable;
/// making the gates satisfiable is later phases' work.
/// </para>
/// </remarks>
public sealed partial class TestGateTests
{
    private const string LocalProcedureRelativePath = "docs/reference/ci.md";

    /// <summary>
    /// There are 26 <c>Assert.SkipWhen</c>/<c>Assert.SkipUnless</c> call sites today — the 26th
    /// is Phase 4.0's own <c>VerifiedBy</c> release gate in
    /// <see cref="PackageVerificationTests.NoPackageIsVerifiedByNothing"/>, added after this
    /// guard was written. Far fewer means the scan lost the tree and is asserting over
    /// nothing — which would pass, silently, forever.
    /// </summary>
    private const int FewestPlausibleSkipGateCalls = 20;

    /// <summary>
    /// The gates name 9 <c>RAGNET_</c> variables today. Same defence as above: a satisfiability
    /// check over an empty variable set is vacuously green.
    /// </summary>
    private const int FewestPlausibleGateVariables = 8;

    /// <summary>
    /// Environment variables that gate tests and that nothing can currently satisfy, recorded so
    /// the suite stays green while the findings stay visible in source. Every entry is a defect in
    /// the repository's coverage, not in this test.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnsatisfiableVariables =
        new(StringComparer.Ordinal)
        {
            // RAGNET_DOCINTEL_ENDPOINT and RAGNET_DOCINTEL_KEY left this ledger on 2026-08-03
            // (Phase 4.1): docs/reference/ci.md now carries a fenced procedure that provisions
            // an Azure Document Intelligence resource (the F0 free tier makes it costless) and
            // sets both variables for the live suite, so the gate is satisfiable by any
            // maintainer. The page also still says plainly that the suite has never run and
            // that its recorded-responses replacement is Phase 6.1's work — satisfiable and
            // exercised are different claims, and only the first is made.
            //
            // RAGNET_TESSDATA left this ledger the same day: docs/reference/ci.md carries a
            // fenced procedure that builds with -p:EnableOcr=true, provisions tessdata and
            // sets the variable, and that procedure was executed green — the real-Tesseract
            // test's first run anywhere. The satisfiability guard covers all three gates again.
        };

    /// <summary>
    /// Conditional-compilation symbols that gate tests and that no build configuration, workflow,
    /// or documented command currently defines.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnsatisfiableSymbols =
        new(StringComparer.Ordinal)
        {
            // ENABLE_OCR left this ledger on 2026-08-03 (Phase 4.1), with RAGNET_TESSDATA above:
            // the decision it was waiting on was taken — the published Rag.NET.Parsers.Pdf
            // package deliberately compiles the Tesseract engine out (consumers get Azure
            // Document Intelligence; features.md and docs/guide/ingestion.md now say so), and
            // the gated test is compiled and run by the fenced -p:EnableOcr=true procedure in
            // docs/reference/ci.md, executed green that day. The satisfiability guard covers
            // the symbol again.
        };

    /// <summary>
    /// The unconditionally skipped facts, each with why it can never run in any environment.
    /// These are legitimate — the environments they need genuinely do not exist in any harness we
    /// run — but an unconditional skip that is not written down is indistinguishable from a
    /// silenced failure, so each is recorded like the allow-lists above and guarded for staleness.
    /// </summary>
    private static readonly PermanentSkip[] RecordedPermanentSkips =
    [
        // Legitimate and documented: Pinecone Local rejects sparse values on dense (dotproduct)
        // indexes over gRPC and silently drops them over REST (verified 2026-07-26 against
        // ghcr.io/pinecone-io/pinecone-local:latest), so the sparse round-trip can only ever run
        // against the real serverless service. The method is kept compiling as the executable
        // contract for such a run, and docs/guide/vector-stores.md records the limitation.
        new(
            "tests/Rag.NET.VectorStores.Pinecone.Tests/PineconeVectorStoreTests.cs",
            "Sparse_StoreAndSearch_RoundTrip",
            "Pinecone Local cannot accept sparse-on-dense upserts in any form, so the test can " +
            "only run against the real serverless service, which no harness reaches today."),

        // Legitimate but previously invisible: the azure-ai-search-simulator container the suite
        // tests against implements no OData filter expressions, so the metadata-filter search can
        // never run there. Unlike the Pinecone skip, this one appeared in no planning record
        // before Phase 4.0 — this entry is its first. Exercising it takes the real Azure AI
        // Search service, which lands with Milestone 4's recorded-responses/live-verification
        // work (replan design §3).
        new(
            "tests/Rag.NET.VectorStores.AzureAISearch.Tests/AzureAISearchVectorStoreTests.cs",
            "Search_WithMetadataFilter_FiltersResults",
            "The azure-ai-search-simulator implements no OData filter expressions, so the " +
            "filtered search can only run against the real service, which no harness reaches " +
            "today. First recorded by Phase 4.0; no earlier planning record mentions it."),
    ];

    /// <summary>
    /// Pins every shape a gate-variable read takes, one live example per shape. Losing a shape
    /// narrows the scan silently — the satisfiability test would still pass, over fewer
    /// variables — so each shape is held by name here instead.
    /// </summary>
    /// <param name="variable">A variable some gate reads in the given shape.</param>
    /// <param name="shape">How the read is written, for the failure message.</param>
    [Theory]
    [InlineData("RAGNET_DOCINTEL_ENDPOINT", "a literal Environment read at the top of the gated test")]
    [InlineData("RAGNET_BEIR_LONG_RUNS", "a read through a named constant (BeirRunBudget.OptInVariable) that the budget gates consult")]
    [InlineData("RAGNET_BEIR_CACHE", "a name pinned by a test while the read itself lives in src/ (BeirDatasetCache.CacheDirectoryVariable)")]
    [InlineData("RAGNET_TESSDATA", "a read inside an #if block that no default build compiles")]
    public void TheScanSeesEveryShapeAGateVariableReadTakes(string variable, string shape)
    {
        Assert.True(
            ScanRepository().Variables.ContainsKey(variable),
            $"The scan no longer finds '{variable}', which a gate reads via {shape}. That shape " +
            "has stopped being scanned, so every gate written that way is now unguarded — widen " +
            "the scan back rather than updating this example.");
    }

    [Fact]
    public void TheScanFindsTheGatingSurface()
    {
        var inventory = ScanRepository();

        Assert.True(
            inventory.SkipCallCount >= FewestPlausibleSkipGateCalls,
            $"Found only {inventory.SkipCallCount} Assert.SkipWhen/SkipUnless call sites under " +
            $"tests/, expected at least {FewestPlausibleSkipGateCalls}. A gate guard that scans " +
            "nothing passes for the wrong reason, so this fails instead.");

        Assert.True(
            inventory.Variables.Count >= FewestPlausibleGateVariables,
            $"Found only {inventory.Variables.Count} RAGNET_ gate variables, expected at least " +
            $"{FewestPlausibleGateVariables}. Either the gates moved or the scan regressed.");
    }

    [Fact]
    public void EveryGateVariableIsSatisfiableSomewhereWrittenDown()
    {
        var inventory = ScanRepository();
        var (automation, documented) = ReadWhereGatesCanBeSatisfied();

        foreach (var (variable, firstSeenIn) in inventory.Variables)
        {
            if (KnownUnsatisfiableVariables.ContainsKey(variable))
            {
                continue;
            }

            Assert.True(
                VariableIsSatisfiable(variable, automation, documented),
                $"'{variable}' gates tests (first seen in {firstSeenIn}) but nothing satisfies " +
                $"it: no command in ci.yml or nightly.yml writes {variable}=…, and no fenced " +
                $"command in {LocalProcedureRelativePath} shows a developer setting it. A " +
                "secrets.* mapping does not count — the repository cannot show the secret " +
                "exists, and that is exactly how three gates skipped on every run while looking " +
                "like coverage. Provision the variable in a workflow step, document the local " +
                $"procedure in a fenced command in {LocalProcedureRelativePath}, or record the " +
                $"gate in {nameof(KnownUnsatisfiableVariables)} with its reason and owner.");
        }
    }

    [Fact]
    public void EveryConditionalCompilationSymbolIsSatisfiableSomewhereWrittenDown()
    {
        var inventory = ScanRepository();
        var (automation, documented) = ReadWhereGatesCanBeSatisfied();

        foreach (var (symbol, firstSeenIn) in inventory.Symbols)
        {
            if (KnownUnsatisfiableSymbols.ContainsKey(symbol))
            {
                continue;
            }

            Assert.True(
                SymbolIsSatisfiable(symbol, automation, documented),
                $"'#if {symbol}' (first seen in {firstSeenIn}) compiles code out of every build " +
                "this repository runs: no build file defines the symbol under a condition " +
                "anything satisfies, no workflow command passes it, and no fenced command in " +
                $"{LocalProcedureRelativePath} shows how to. A test behind such a symbol is not " +
                "even skipped — it is not compiled, and appears in no test count anywhere. " +
                "Define the symbol somewhere real, document the build that defines it, or " +
                $"record it in {nameof(KnownUnsatisfiableSymbols)} with its reason and owner.");
        }
    }

    [Fact]
    public void EveryUnconditionallySkippedFactIsRecordedWithItsReason()
    {
        foreach (var site in ScanRepository().SkipAttributeSites)
        {
            Assert.True(
                IsRecorded(site),
                $"{site.RelativePath} skips '{site.Method}' unconditionally via [Fact(Skip=…)] " +
                $"and {nameof(RecordedPermanentSkips)} has no entry for it. An unconditional " +
                "skip can never run in any environment, which is legitimate only when that is " +
                "recorded and owned — otherwise it is indistinguishable from a silenced " +
                "failure. Record it with the reason it can never run, or gate it on a condition " +
                "something satisfies.");
        }
    }

    [Fact]
    public void EveryRecordedUnsatisfiableGateIsStillUnsatisfiable()
    {
        // The ledgers keep the suite green without hiding the findings; this keeps the ledgers
        // honest in both directions. An entry whose gate something now satisfies, or whose gate
        // has left the source, is stale — and a stale entry is a hole in the guard, so it fails
        // here until it is deleted.
        var inventory = ScanRepository();
        var (automation, documented) = ReadWhereGatesCanBeSatisfied();

        foreach (var (variable, reason) in KnownUnsatisfiableVariables)
        {
            Assert.True(
                inventory.Variables.ContainsKey(variable),
                $"No gate reads '{variable}' any more, so its entry in " +
                $"{nameof(KnownUnsatisfiableVariables)} is stale. Delete the entry. It was " +
                $"recorded because: {reason}");

            Assert.False(
                VariableIsSatisfiable(variable, automation, documented),
                $"'{variable}' is satisfiable now, so its entry in " +
                $"{nameof(KnownUnsatisfiableVariables)} is stale. Delete the entry so " +
                $"{nameof(EveryGateVariableIsSatisfiableSomewhereWrittenDown)} guards the gate " +
                $"again. It was recorded because: {reason}");
        }

        foreach (var (symbol, reason) in KnownUnsatisfiableSymbols)
        {
            Assert.True(
                inventory.Symbols.ContainsKey(symbol),
                $"No source uses '#if {symbol}' any more, so its entry in " +
                $"{nameof(KnownUnsatisfiableSymbols)} is stale. Delete the entry. It was " +
                $"recorded because: {reason}");

            Assert.False(
                SymbolIsSatisfiable(symbol, automation, documented),
                $"'{symbol}' is definable now, so its entry in " +
                $"{nameof(KnownUnsatisfiableSymbols)} is stale. Delete the entry so " +
                $"{nameof(EveryConditionalCompilationSymbolIsSatisfiableSomewhereWrittenDown)} " +
                $"guards the symbol again. It was recorded because: {reason}");
        }
    }

    [Fact]
    public void EveryRecordedPermanentSkipStillExists()
    {
        var sites = ScanRepository().SkipAttributeSites;

        foreach (var record in RecordedPermanentSkips)
        {
            var found = false;
            foreach (var site in sites)
            {
                if (Matches(record, site))
                {
                    found = true;
                    break;
                }
            }

            Assert.True(
                found,
                $"There is no [Fact(Skip=…)] on '{record.Method}' in {record.RelativePath} any " +
                $"more — the skip was removed or made conditional, so its entry in " +
                $"{nameof(RecordedPermanentSkips)} is stale. Delete the entry. It was recorded " +
                $"because: {record.Reason}");
        }
    }

    private static bool IsRecorded(SkipSite site)
    {
        foreach (var record in RecordedPermanentSkips)
        {
            if (Matches(record, site))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(PermanentSkip record, SkipSite site) =>
        string.Equals(record.RelativePath, site.RelativePath, StringComparison.Ordinal) &&
        string.Equals(record.Method, site.Method, StringComparison.Ordinal);

    /// <summary>
    /// Scans the working tree for the gating surface: skip calls, skip attributes, the
    /// <c>RAGNET_</c> variables gates read, and custom conditional-compilation symbols.
    /// </summary>
    /// <returns>The inventory.</returns>
    /// <remarks>
    /// Variables come from <c>tests/</c> only — the <c>RAGNET_</c> namespace is the gate
    /// namespace by convention, and <see cref="TestProjectTierTests"/> already holds reads and
    /// <c>RequiresSecrets</c> declarations together. Symbols come from <c>src/</c> as well,
    /// because <c>ENABLE_OCR</c> compiles the production Tesseract engine out along with its
    /// test, and the two must not drift apart silently.
    /// </remarks>
    private static GateInventory ScanRepository()
    {
        var inventory = new GateInventory();

        foreach (var (relativePath, fullPath) in EnumerateSourceFiles("tests"))
        {
            var text = File.ReadAllText(fullPath);
            inventory.SkipCallCount += SkipGateCall().Count(text);
            AddVariables(inventory, relativePath, text);
            AddSymbols(inventory, relativePath, text);
            AddSkipAttributeSites(inventory, relativePath, text);
        }

        foreach (var (relativePath, fullPath) in EnumerateSourceFiles("src"))
        {
            AddSymbols(inventory, relativePath, File.ReadAllText(fullPath));
        }

        return inventory;
    }

    private static void AddVariables(GateInventory inventory, string relativePath, string text)
    {
        // Two read shapes. The direct call covers most gates; the exact-literal shape covers the
        // reads that go through a named constant — BeirRunBudget.OptInVariable in tests, and
        // BeirDatasetCache.CacheDirectoryVariable in src, whose name a unit test pins as an exact
        // string. Prose mentions ("Set RAGNET_X to …" in skip messages) never match either shape,
        // because neither begins a string literal with the name and nothing else.
        foreach (Match match in DirectVariableRead().Matches(text))
        {
            inventory.RecordVariable(match.Groups["name"].Value, relativePath);
        }

        foreach (Match match in ExactVariableLiteral().Matches(text))
        {
            inventory.RecordVariable(match.Groups["name"].Value, relativePath);
        }
    }

    private static void AddSymbols(GateInventory inventory, string relativePath, string text)
    {
        foreach (Match directive in ConditionalDirective().Matches(text))
        {
            foreach (Match token in IdentifierToken().Matches(directive.Groups["expression"].Value))
            {
                if (!IsSdkDefinedSymbol(token.Value))
                {
                    inventory.RecordSymbol(token.Value, relativePath);
                }
            }
        }
    }

    private static void AddSkipAttributeSites(GateInventory inventory, string relativePath, string text)
    {
        foreach (Match match in SkipAttribute().Matches(text))
        {
            var method = MethodDeclaration().Match(text, match.Index);
            inventory.SkipAttributeSites.Add(new SkipSite(
                relativePath,
                method.Success ? method.Groups["method"].Value : "(no method declaration found)"));
        }
    }

    /// <summary>
    /// The symbols the SDK defines for every build — framework monikers, configuration names —
    /// which are satisfied by construction and are not gates.
    /// </summary>
    /// <param name="symbol">The symbol found in an <c>#if</c> expression.</param>
    /// <returns>Whether the build system itself defines it.</returns>
    private static bool IsSdkDefinedSymbol(string symbol) =>
        string.Equals(symbol, "true", StringComparison.Ordinal) ||
        string.Equals(symbol, "false", StringComparison.Ordinal) ||
        string.Equals(symbol, "DEBUG", StringComparison.Ordinal) ||
        string.Equals(symbol, "TRACE", StringComparison.Ordinal) ||
        string.Equals(symbol, "RELEASE", StringComparison.Ordinal) ||
        symbol.StartsWith("NET", StringComparison.Ordinal);

    /// <summary>
    /// Reads the two places a gate may be satisfied: workflow commands (comments stripped, so
    /// prose cannot satisfy an assertion about what runs), and the fenced command blocks of the
    /// CI documentation page — a runnable local procedure, as opposed to a sentence mentioning a
    /// flag, which is exactly what must not count.
    /// </summary>
    /// <returns>The workflow commands and the fenced documentation commands.</returns>
    private static (string Automation, string Documented) ReadWhereGatesCanBeSatisfied()
    {
        var automation = new StringBuilder();

        foreach (var workflow in WorkflowFileNames)
        {
            var path = TestProject.WorkflowPath(workflow);
            if (File.Exists(path))
            {
                _ = automation.Append(TestProject.ReadWorkflowCommands(path)).Append(' ');
            }
        }

        return (automation.ToString(), ReadFencedDocumentationCommands());
    }

    private static string ReadFencedDocumentationCommands()
    {
        var path = Path.Combine(
            TestProject.FindRepositoryRoot(), "docs", "reference", "ci.md");
        if (!File.Exists(path))
        {
            return string.Empty;
        }

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
    /// A variable is satisfiable when something writes it a value: <c>NAME=…</c> in a workflow
    /// command (the GITHUB_ENV provisioning steps have this shape) or in a fenced documentation
    /// command. A <c>NAME: ${{ secrets.NAME }}</c> mapping deliberately does not match — the
    /// repository cannot show a secret exists, and the unprovisioned ones measurably do not.
    /// </summary>
    /// <param name="variable">The variable name.</param>
    /// <param name="automation">The workflow commands.</param>
    /// <param name="documented">The fenced documentation commands.</param>
    /// <returns>Whether anything sets the variable.</returns>
    private static bool VariableIsSatisfiable(string variable, string automation, string documented)
    {
        var setter = variable + "=";

        return automation.Contains(setter, StringComparison.Ordinal) ||
            documented.Contains(setter, StringComparison.Ordinal);
    }

    /// <summary>
    /// A symbol is satisfiable when a workflow or fenced documentation command names it directly
    /// (a <c>DefineConstants</c> pass-through), or when some build file defines it under
    /// conditions something can meet — unconditionally, or gated on a property that a workflow or
    /// fenced command sets to true (<c>/p:EnableOcr=true</c> would match its
    /// <c>'$(EnableOcr)'=='true'</c> condition).
    /// </summary>
    /// <param name="symbol">The conditional-compilation symbol.</param>
    /// <param name="automation">The workflow commands.</param>
    /// <param name="documented">The fenced documentation commands.</param>
    /// <returns>Whether any build this repository describes defines the symbol.</returns>
    private static bool SymbolIsSatisfiable(string symbol, string automation, string documented)
    {
        if (automation.Contains(symbol, StringComparison.Ordinal) ||
            documented.Contains(symbol, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var buildFile in EnumerateBuildFiles())
        {
            foreach (var element in XDocument.Load(buildFile).Descendants())
            {
                if (string.Equals(element.Name.LocalName, "DefineConstants", StringComparison.Ordinal) &&
                    element.Value.Contains(symbol, StringComparison.Ordinal) &&
                    DefinitionConditionsAreSatisfiable(element, automation, documented))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool DefinitionConditionsAreSatisfiable(
        XElement definition, string automation, string documented)
    {
        for (XElement? node = definition; node is not null; node = node.Parent)
        {
            var condition = node.Attribute("Condition")?.Value;
            if (condition is not null && !ConditionCanBeMet(condition, automation, documented))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConditionCanBeMet(string condition, string automation, string documented)
    {
        foreach (Match property in PropertyReference().Matches(condition))
        {
            var setter = property.Groups["property"].Value + "=true";
            if (automation.Contains(setter, StringComparison.Ordinal) ||
                documented.Contains(setter, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string RelativePath, string FullPath)> EnumerateSourceFiles(string topDirectory)
    {
        foreach (var (relativePath, fullPath) in TestProject.EnumerateSourceFiles(topDirectory))
        {
            // The one self-exemption, and it is load-bearing: this file names the recorded gate
            // variables as exact string literals, so scanning it would count the ledger itself as
            // a gate read — and a staleness entry that satisfies its own "still read by a gate"
            // check can never die. The guard must not read itself.
            if (relativePath.EndsWith("/TestGateTests.cs", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relativePath, fullPath);
        }
    }

    private static IEnumerable<string> EnumerateBuildFiles()
    {
        var root = TestProject.FindRepositoryRoot();

        foreach (var name in RootBuildFileNames)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var topDirectory in SourceTopDirectories)
        {
            foreach (var pattern in BuildFilePatterns)
            {
                foreach (var file in Directory.EnumerateFiles(
                    Path.Combine(root, topDirectory), pattern, SearchOption.AllDirectories))
                {
                    if (!IsBuildOutput(Path.GetRelativePath(root, file).Replace('\\', '/')))
                    {
                        yield return file;
                    }
                }
            }
        }
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Contains("/bin/", StringComparison.Ordinal) ||
        relativePath.Contains("/obj/", StringComparison.Ordinal);

    private static readonly string[] WorkflowFileNames = ["ci.yml", "nightly.yml"];
    private static readonly string[] RootBuildFileNames = ["Directory.Build.props", "Directory.Build.targets"];
    private static readonly string[] SourceTopDirectories = ["src", "tests"];
    private static readonly string[] BuildFilePatterns = ["*.csproj", "*.props", "*.targets"];

    /// <summary>Matches a literal read of a <c>RAGNET_</c> environment variable.</summary>
    /// <remarks>
    /// The escapes make this pattern unable to match its own source text or the similar pattern
    /// in <see cref="TestProject"/> — the same trap those document, avoided the same way.
    /// </remarks>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"Environment\.GetEnvironmentVariable\(""(?<name>RAGNET_[A-Z0-9_]+)""", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex DirectVariableRead();

    /// <summary>
    /// Matches a string literal that is exactly one <c>RAGNET_</c> variable name — the shape of a
    /// named constant's value and of a test pinning such a constant. Skip-message prose never
    /// matches, because the name there is mid-sentence rather than the whole literal.
    /// </summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"""(?<name>RAGNET_[A-Z0-9_]+)""", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex ExactVariableLiteral();

    /// <summary>Matches one conditional skip call site.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"Assert\.Skip(?:When|Unless)\(", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SkipGateCall();

    /// <summary>Matches an unconditional skip attribute.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"\[(?:Fact|Theory)\(\s*Skip\s*=", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SkipAttribute();

    /// <summary>Matches the method declaration following a skip attribute.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"public\s+(?:async\s+)?\w+\s+(?<method>\w+)\s*\(", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex MethodDeclaration();

    /// <summary>Matches an <c>#if</c>/<c>#elif</c> directive and captures its expression.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"^[ \t]*#(?:el)?if[ \t]+(?<expression>[^\r\n]*)", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking | RegexOptions.Multiline)]
    private static partial Regex ConditionalDirective();

    /// <summary>Matches one identifier inside an <c>#if</c> expression.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex IdentifierToken();

    /// <summary>Matches one <c>$(Property)</c> reference in an MSBuild condition.</summary>
    /// <returns>The compiled matcher.</returns>
    [GeneratedRegex(@"\$\((?<property>\w+)\)", RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex PropertyReference();

    /// <summary>What the scan of the working tree found.</summary>
    private sealed class GateInventory
    {
        /// <summary>Gets the gate variables, each with the file it was first seen in.</summary>
        public SortedDictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the custom conditional-compilation symbols, each with its first file.</summary>
        public SortedDictionary<string, string> Symbols { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the unconditional skip attribute sites.</summary>
        public List<SkipSite> SkipAttributeSites { get; } = [];

        /// <summary>Gets or sets the number of conditional skip call sites.</summary>
        public int SkipCallCount { get; set; }

        public void RecordVariable(string name, string relativePath) =>
            _ = Variables.TryAdd(name, relativePath);

        public void RecordSymbol(string name, string relativePath) =>
            _ = Symbols.TryAdd(name, relativePath);
    }

    /// <summary>One unconditional skip site found by the scan.</summary>
    /// <param name="RelativePath">The file, relative to the repository root.</param>
    /// <param name="Method">The skipped test method.</param>
    private sealed record SkipSite(string RelativePath, string Method);

    /// <summary>One recorded permanent skip.</summary>
    /// <param name="RelativePath">The file, relative to the repository root.</param>
    /// <param name="Method">The skipped test method.</param>
    /// <param name="Reason">Why the test can never run in any environment we operate.</param>
    private sealed record PermanentSkip(string RelativePath, string Method, string Reason);
}
